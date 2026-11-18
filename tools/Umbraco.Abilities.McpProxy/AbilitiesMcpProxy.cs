using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Umbraco.Abilities.McpProxy;

/// <summary>
/// What the Umbraco CMS MCP Dev server would add to use abilities: one MCP tool per ability a site lists, each call
/// forwarded to the Management API. It knows nothing about any ability or product. A new ability on the site is a new
/// tool here, with no change to this code.
/// <para>
/// One proxy is one MCP session. It remembers the change set the session has open, as the AI tools' conversation does,
/// and names it in the <c>Umb-Change-Set</c> header of every call that doesn't name one. Each HTTP call stands alone,
/// so without this a model would have to pass <c>changeSetId</c> to every call.
/// </para>
/// <para>
/// A call that can't be undone runs only once the person agrees. The site decides which calls those are (a tool that
/// requires approval, run live rather than recorded in a change set) and answers 428 without doing anything, with the
/// question to ask and an approval id. The proxy asks the person through the MCP client (elicitation), not the model, so
/// the model never sees the id and can't agree for them. On yes it sends the call again with the id in
/// <c>Umb-Approval</c>; on no, or with a client that can't ask, it stops there. If the change set changed in between,
/// the site asks again, with what it now holds.
/// </para>
/// </summary>
public sealed class AbilitiesMcpProxy(HttpClient umbraco)
{
    /// <summary>The Management API path the abilities are listed at, and called under.</summary>
    public const string AbilitiesPath = "/umbraco/management/api/v1/abilities";

    private const string ChangeSetHeader = "Umb-Change-Set";
    private const string ApprovalHeader = "Umb-Approval";

    // A set that keeps changing while the person is asked would ask forever.
    private const int MaxAsks = 3;
    private const string ChangeSetIdArgument = "changeSetId";

    private Dictionary<string, ListedAbility> _abilities = [];

    /// <summary>The change set this session works in when a call doesn't name one. Null when none is open.</summary>
    public Guid? OpenChangeSetId { get; private set; }

    /// <summary>The MCP server options: tools listed from the site, and calls forwarded to it.</summary>
    public McpServerOptions CreateOptions() => new()
    {
        ServerInfo = new Implementation { Name = "umbraco-abilities", Version = "0.1.0" },
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = async (_, cancellationToken) => new ListToolsResult
            {
                Tools = (await ListAsync(cancellationToken)).Values.Select(ability => ability.Tool).ToList(),
            },
            CallToolHandler = async (request, cancellationToken) => await CallAsync(request.Server, request.Params!, cancellationToken),
        },
    };

    private async Task<Dictionary<string, ListedAbility>> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await umbraco.GetFromJsonAsync<JsonElement>(AbilitiesPath, cancellationToken);
        _abilities = listed.EnumerateArray().Select(ListedAbility.From).ToDictionary(ability => ability.Tool.Name);
        return _abilities;
    }

    private async Task<CallToolResult> CallAsync(McpServer session, CallToolRequestParams call, CancellationToken cancellationToken)
    {
        if (!_abilities.TryGetValue(call.Name, out var ability) && !(await ListAsync(cancellationToken)).TryGetValue(call.Name, out ability))
        {
            return Error($"No tool '{call.Name}'.");
        }

        var input = new JsonObject();
        foreach (var (name, value) in call.Arguments ?? new Dictionary<string, JsonElement>())
        {
            input[name] = JsonNode.Parse(value.GetRawText());
        }

        var response = await SendAsync(ability, input, approvalId: null, cancellationToken);
        for (var asks = 0; response.StatusCode == HttpStatusCode.PreconditionRequired; asks++)
        {
            // The site needs the person's consent for this call, and did nothing. Ask, then send it again with the id.
            var approval = TryParse(await response.Content.ReadAsStringAsync(cancellationToken), out var refusal)
                && refusal.TryGetProperty("approval", out var request) && request.ValueKind == JsonValueKind.Object
                ? request
                : default;
            response.Dispose();
            if (approval.ValueKind != JsonValueKind.Object || asks == MaxAsks)
            {
                return Error($"{ability.Name} needs the person's consent, and it couldn't be settled. Nothing was done.");
            }

            if (await AskAsync(session, ability, approval.GetProperty("question").GetString()!, cancellationToken) is { } refused)
            {
                return refused;
            }

            response = await SendAsync(ability, input, approval.GetProperty("id").GetGuid(), cancellationToken);
        }

        using var _ = response;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!TryParse(text, out var body) || !body.TryGetProperty("success", out var success))
        {
            // Not an ability's result: the site refused the request itself (signed out, no access to the API).
            return Error($"Umbraco answered {(int)response.StatusCode} {response.ReasonPhrase}. {text}".Trim());
        }

        Remember(body);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = ForModels(text) }],
            IsError = !success.GetBoolean(),
        };
    }

    private async Task<HttpResponseMessage> SendAsync(ListedAbility ability, JsonObject input, Guid? approvalId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AbilitiesPath}/{Uri.EscapeDataString(ability.Id)}/invoke")
        {
            Content = JsonContent.Create(input),
        };
        if (ability.UsesChangeSet && !input.ContainsKey(ChangeSetIdArgument) && OpenChangeSetId is { } open)
        {
            request.Headers.Add(ChangeSetHeader, open.ToString());
        }

        if (approvalId is { } id)
        {
            request.Headers.Add(ApprovalHeader, id.ToString());
        }

        return await umbraco.SendAsync(request, cancellationToken);
    }

    // Asks the person, through their MCP client, the site's question about one call. Null when they said yes.
    private static async Task<CallToolResult?> AskAsync(McpServer session, ListedAbility ability, string question, CancellationToken cancellationToken)
    {
        if (session.ClientCapabilities?.Elicitation is null)
        {
            return Error($"{ability.Name} can't be undone, so the person must agree to it first, but this MCP client can't ask them. Nothing was done.");
        }

        var answer = await session.ElicitAsync(new ElicitRequestParams
        {
            Message = question,
            RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>() },
        }, cancellationToken);

        return answer.IsAccepted
            ? null
            : Error($"The person didn't agree to {ability.Name} ({answer.Action}). Nothing was done. Don't try again unless they ask.");
    }

    // The set a call created or used stays open for the session; one applied or discarded is closed.
    private void Remember(JsonElement body)
    {
        if (!body.TryGetProperty("changeSet", out var changeSet) || changeSet.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var id = changeSet.GetProperty("id").GetGuid();
        if (changeSet.GetProperty("open").GetBoolean())
        {
            OpenChangeSetId = id;
        }
        else if (OpenChangeSetId == id)
        {
            OpenChangeSetId = null;
        }
    }

    private static bool TryParse(string text, out JsonElement body)
    {
        try
        {
            body = JsonDocument.Parse(text).RootElement.Clone();
            return body.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            body = default;
            return false;
        }
    }

    // The site's schemas carry x-editor and x-meta for UIs, such as a form built from an input schema. A model doesn't
    // need them: they cost tokens and could mislead it. MCP Dev would strip them the same way.
    private static readonly string[] UiKeywords = ["x-editor", "x-meta"];

    private static string ForModels(string json)
        => UiKeywords.Any(k => json.Contains($"\"{k}\"", StringComparison.Ordinal)) ? Strip(JsonNode.Parse(json))!.ToJsonString() : json;

    private static JsonNode? Strip(JsonNode? node)
    {
        if (node is JsonObject json)
        {
            foreach (var keyword in UiKeywords)
            {
                json.Remove(keyword);
            }
        }

        foreach (var child in node switch { JsonObject o => o.Select(p => p.Value), JsonArray a => a, _ => [] })
        {
            Strip(child);
        }

        return node;
    }

    private static CallToolResult Error(string message)
        => new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    /// <summary>
    /// One ability as the site lists it, and the MCP tool made from it. Its kind and approval flag become MCP's tool
    /// hints: a read is read only, and a write that can't be undone is destructive, so a client knows to ask the person
    /// first. A write a change set can undo is not destructive.
    /// </summary>
    private sealed record ListedAbility(string Id, string Name, bool UsesChangeSet, Tool Tool)
    {
        public static ListedAbility From(JsonElement listed)
        {
            var readOnly = listed.GetProperty("kind").GetString() == "Read";
            return new(
                listed.GetProperty("id").GetString()!,
                listed.GetProperty("name").GetString()!,
                listed.GetProperty("usesChangeSet").GetBoolean(),
                new Tool
                {
                    Name = listed.GetProperty("toolName").GetString()!,
                    Description = listed.GetProperty("description").GetString(),
                    InputSchema = JsonSerializer.SerializeToElement(Strip(JsonNode.Parse(listed.GetProperty("inputSchema").GetRawText()))),
                    Annotations = new ToolAnnotations
                    {
                        Title = listed.GetProperty("name").GetString(),
                        ReadOnlyHint = readOnly,
                        DestructiveHint = readOnly ? null : listed.GetProperty("requiresApproval").GetBoolean(),
                    },
                });
        }
    }
}
