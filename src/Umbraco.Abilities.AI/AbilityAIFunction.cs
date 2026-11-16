using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities.AI;

/// <summary>
/// What one conversation keeps between tool calls: the user it acts for, and the change sets open in it. The surface
/// creates one per conversation, and may open a change set up front with <see cref="Open"/>.
/// </summary>
public sealed class AbilityToolSession(Guid userKey)
{
    private readonly List<Guid> _open = [];

    /// <summary>The user the model acts for. It gets their permissions, no more.</summary>
    public Guid UserKey { get; set; } = userKey;

    /// <summary>
    /// The change sets created or named in this conversation that are still open, oldest first. A set leaves once it
    /// is applied or discarded.
    /// </summary>
    public IReadOnlyList<Guid> OpenChangeSetIds => _open;

    /// <summary>
    /// The change set calls work in when they don't name one: the only set open in this conversation. Null when none is
    /// open, or when several are: then every call must name one.
    /// </summary>
    public Guid? ChangeSetId => _open.Count == 1 ? _open[0] : null;

    /// <summary>Adds a change set to the conversation, as creating or naming one does.</summary>
    public void Open(Guid changeSetId)
    {
        if (!_open.Contains(changeSetId))
        {
            _open.Add(changeSetId);
        }
    }

    /// <summary>Takes a change set out of the conversation. The set itself is untouched.</summary>
    public void Close(Guid changeSetId) => _open.Remove(changeSetId);
}

/// <summary>
/// Exposes one ability as an M.E.AI tool. Nothing here is specific to an ability: every ability a package
/// declares becomes a tool the same way. An Automate or MCP adapter would be the same size.
/// <para>
/// Every tool that uses a change set (<see cref="AbilityAttribute.UsesChangeSet"/>) also takes an optional <c>changeSetId</c>, added here so no ability declares it. A call works in the set it
/// names, or else the only set open in the conversation, which is ambient for that call only
/// (<see cref="IChangeSetAccessor"/>). With two or more open, a call must name one, so an edit never lands in a set the
/// model didn't mean. Every result says which set the call used.
/// </para>
/// </summary>
public sealed class AbilityAIFunction : AIFunction
{
    /// <summary>The argument every tool takes to name the change set it works in.</summary>
    public const string ChangeSetIdArgument = "changeSetId";

    private const string ChangeSetIdDescription =
        "The change set to work in. Leave it out when only one change set is open in this conversation, and that one is " +
        "used. Required when two or more are open. Every result says which change set it used.";

    private readonly IAbility _ability;
    private readonly AbilityInvoker _invoker;
    private readonly AbilityToolSession _session;
    private readonly bool _takesChangeSet;

    public AbilityAIFunction(IAbility ability, AbilityInvoker invoker, AbilityToolSession session)
    {
        _ability = ability;
        _invoker = invoker;
        _session = session;

        // Abilities that don't use a change set (describing a schema, starting a set) take no id and always run outside one.
        _takesChangeSet = ability.Descriptor.UsesChangeSet;
        var schema = AIJsonUtilities.CreateJsonSchema(ability.Descriptor.InputType, serializerOptions: AbilityJson.Options);
        JsonSchema = _takesChangeSet ? WithChangeSetId(schema) : schema;
        AdditionalProperties = new Dictionary<string, object?>
        {
            ["abilityId"] = ability.Descriptor.Id,
            ["kind"] = ability.Descriptor.Kind.ToString(),
            ["requiresApproval"] = ability.Descriptor.RequiresApproval,
        };
    }

    /// <summary>Tool names allow letters, digits, _ and -, so <c>entity.addItem</c> becomes <c>entity_addItem</c>.</summary>
    public override string Name => ToToolName(_ability.Descriptor.Id);

    public override string Description => _ability.Descriptor.Description;

    public override JsonElement JsonSchema { get; }

    public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; }

    public AbilityDescriptor Descriptor => _ability.Descriptor;

    public static string ToToolName(string abilityId) => abilityId.Replace('.', '_');

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var changeSets = _invoker.ChangeSets;
        var input = new JsonObject();
        Guid? named = null;
        foreach (var (name, value) in arguments)
        {
            if (_takesChangeSet && name == ChangeSetIdArgument)
            {
                if (value is null || value is JsonElement { ValueKind: JsonValueKind.Null })
                {
                    continue;
                }

                if (!Guid.TryParse(value is JsonElement element ? element.ToString() : value.ToString(), out var id))
                {
                    return Payload(AbilityResult.Fail(AbilityOperationStatus.InvalidInput, "changeSetId must be a change set's id.",
                        [new Problem(ChangeSetIdArgument, "Not an id.")]), null);
                }

                named = id;
                continue;
            }

            input[name] = value switch
            {
                null => null,
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                JsonNode node => node.DeepClone(),
                _ => JsonSerializer.SerializeToNode(value, AbilityJson.Options),
            };
        }

        // Sets applied or discarded since the last call (here or elsewhere) are no longer open.
        foreach (var closed in _session.OpenChangeSetIds.Where(id => changeSets.Find(id) is null).ToList())
        {
            _session.Close(closed);
        }

        ChangeSet? used;
        if (named is { } given)
        {
            // A set that is named must exist: never fall back to live data because of a wrong or stale id.
            used = changeSets.Find(given);
            if (used is null)
            {
                return Payload(AbilityResult.Fail(AbilityOperationStatus.InvalidInput,
                    $"No change set {given}. It may have been applied or discarded.",
                    [new Problem(ChangeSetIdArgument, "Not found.")]), null);
            }

            _session.Open(given);
        }
        else if (_takesChangeSet && _session.OpenChangeSetIds.Count > 1)
        {
            // Guessing between two open sets could put an edit in the wrong one, so ask.
            var open = string.Join(", ", _session.OpenChangeSetIds.Select(id => $"'{changeSets.Get(id).Name}' ({id})"));
            return Payload(AbilityResult.Fail(AbilityOperationStatus.InvalidInput,
                $"{_session.OpenChangeSetIds.Count} change sets are open in this conversation: {open}. Name the one to use with changeSetId.",
                [new Problem(ChangeSetIdArgument, "Required while more than one change set is open.")]), null);
        }
        else
        {
            used = _takesChangeSet && _session.ChangeSetId is { } only ? changeSets.Find(only) : null;
        }

        AbilityResult result;
        using (changeSets.Use(used?.Id))
        {
            result = await _invoker.InvokeAsync(_ability.Descriptor.Id, input, new AbilityContext(_session.UserKey), cancellationToken);
        }

        var usedSet = used is null ? null : new ChangeSetUsed(used.Id, used.Name);
        if (result.Result is ChangeSetCreated created)
        {
            _session.Open(created.ChangeSetId);
            usedSet = new ChangeSetUsed(created.ChangeSetId, created.Name);
        }
        else if (used is not null && changeSets.Find(used.Id) is null)
        {
            // Applied or discarded: it leaves the conversation.
            _session.Close(used.Id);
            usedSet = usedSet! with { Open = false };
        }

        return Payload(result, usedSet);
    }

    // Failures go back to the model as data, so it can correct Input failures and stop on the others.
    // The same shape as the CMS's Attempt: a status and a result, plus a message and problems when it failed.
    private static JsonElement Payload(AbilityResult result, ChangeSetUsed? changeSet)
        => JsonSerializer.SerializeToElement(
            result.Success
                ? new { success = true, status = result.Status.ToString(), result = result.Result, changeSet }
                : (object)new
                {
                    success = false,
                    status = result.Status.ToString(),
                    correctable = result.Correctable,
                    message = result.Message,
                    problems = result.Problems,
                    changeSet,
                },
            AbilityJson.Options);

    private static JsonElement WithChangeSetId(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (node["properties"] is not JsonObject properties)
        {
            node["properties"] = properties = [];
        }

        properties[ChangeSetIdArgument] = new JsonObject
        {
            ["type"] = "string",
            ["format"] = "uuid",
            ["description"] = ChangeSetIdDescription,
        };
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>The change set a call worked in, as every result reports it. Open is false once applied or discarded.</summary>
    private sealed record ChangeSetUsed(Guid Id, string Name, bool Open = true);
}

public static class AbilityAITools
{
    /// <summary>
    /// Every ability as a tool, for one conversation. Abilities that need approval are wrapped so a chat client pauses
    /// for a person, the same mechanism Umbraco.AI's HITL approval uses.
    /// </summary>
    public static IReadOnlyList<AIFunction> Create(AbilityRegistry abilities, AbilityInvoker invoker, AbilityToolSession session)
        => abilities.All.Select(ability =>
        {
            AIFunction function = new AbilityAIFunction(ability, invoker, session);
#pragma warning disable MEAI001 // Approval support in M.E.AI is experimental.
            return ability.Descriptor.RequiresApproval ? new ApprovalRequiredAIFunction(function) : function;
#pragma warning restore MEAI001
        }).ToList();
}
