using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.AI;
using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// Plays the part of a model: calls the AI tools by name with JSON arguments, exactly as a chat client's
/// function invocation would. One agent is one conversation, with its own current change set.
/// </summary>
public sealed class ScriptedAgent
{
    private readonly SampleSite _site;

    public ScriptedAgent(SampleSite site)
    {
        _site = site;
        // The AI package's consumer, on the site, as its composer would add it.
        if (site.Consumers.Find(AiChatConsumer.ConsumerAlias) is null)
        {
            site.Consumers.Add(new AiChatConsumer());
        }

        Tools = AbilityAITools
            .Create(site.Tools, site.Runner, Session)
            .ToDictionary(t => t.Name);
    }

    public IReadOnlyDictionary<string, AIFunction> Tools { get; }

    /// <summary>What the conversation keeps between calls: its user and current change set.</summary>
    public AbilityToolSession Session { get; } = new(SampleAccess.Editor);

    /// <summary>
    /// The conversation's current change set: the only one open in it. Setting it plays a surface that opened one (and
    /// closed any others), or with null, closed them all.
    /// </summary>
    public Guid? ChangeSetId
    {
        get => Session.ChangeSetId;
        set
        {
            foreach (var open in Session.OpenChangeSetIds.ToList())
            {
                Session.Close(open);
            }

            if (value is { } id)
            {
                Session.Open(id);
            }
        }
    }

    /// <summary>The user the agent acts for. It gets their permissions, no more.</summary>
    public Guid UserKey
    {
        get => Session.UserKey;
        set => Session.UserKey = value;
    }

    public async Task<JsonElement> CallAsync(string tool, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));

        var result = await Tools[tool].InvokeAsync(arguments);
        return (JsonElement)result!;
    }

    /// <summary>
    /// Calls an ability as a UI would, through the Management API, in the same conversation: the same result a model
    /// gets, but with the keywords meant for UIs (<c>x-editor</c>, <c>x-meta</c>) left in. Returns its output.
    /// </summary>
    public async Task<JsonElement> UiOkAsync(string tool, string argumentsJson)
    {
        var result = (await _site.Runner.InvokeAsync(UiTool(tool), JsonNode.Parse(argumentsJson)!.AsObject(), Session, AbilityConsent.Given)).ToJson();
        Assert.True(result.GetProperty("success").GetBoolean(), $"{tool} failed: {result}");
        return result.GetProperty("result");
    }

    /// <summary>An ability's tool as a UI reads it: its input schema keeps the keywords meant for UIs.</summary>
    public AbilityTool UiTool(string tool)
        => new AbilityTool(_site.Abilities.All.Single(a => AbilityTool.ToToolName(a.Descriptor.Id) == tool)).WithChangeSetIdArgument();

    /// <summary>Calls a tool and asserts it succeeded. Returns its output.</summary>
    public async Task<JsonElement> OkAsync(string tool, string argumentsJson)
    {
        var result = await CallAsync(tool, argumentsJson);
        Assert.True(result.GetProperty("success").GetBoolean(), $"{tool} failed: {result}");
        return result.GetProperty("result");
    }

    /// <summary>Calls a tool and asserts it failed. Returns the failure.</summary>
    public async Task<JsonElement> FailAsync(string tool, string argumentsJson)
    {
        var result = await CallAsync(tool, argumentsJson);
        Assert.False(result.GetProperty("success").GetBoolean(), $"{tool} should have failed: {result}");
        return result;
    }

    public async Task<Guid> StartChangeSetAsync(string name)
    {
        // The new set joins the conversation: nothing here copies the id.
        var output = await OkAsync("changeSet_create", $$"""{ "name": "{{name}}" }""");
        var id = output.GetProperty("changeSetId").GetGuid();
        Assert.Contains(id, Session.OpenChangeSetIds);
        return id;
    }
}

public static class JsonAssert
{
    public static IReadOnlyList<string> ProblemPaths(JsonElement validateOutput)
        => validateOutput.GetProperty("entities").EnumerateArray()
            .SelectMany(e => e.GetProperty("problems").EnumerateArray())
            .Select(p => p.GetProperty("path").GetString()!)
            .ToList();
}
