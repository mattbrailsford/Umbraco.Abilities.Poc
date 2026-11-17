using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities;

/// <summary>
/// What one conversation keeps between tool calls: the user it acts for, and the change sets open in it. The surface
/// creates one per conversation, and may open a change set up front with <see cref="Open"/>. A surface where each call
/// stands alone, such as HTTP, creates one per call.
/// </summary>
public sealed class AbilityToolSession(Guid userKey)
{
    private readonly List<Guid> _open = [];

    /// <summary>The user the caller acts for. It gets their permissions, no more.</summary>
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
/// One ability as a tool, for any surface: an AI tool, a Management API endpoint, an MCP tool, an Automate action.
/// Each surface wraps it; none repeats it. It gives the tool's name, description and input schema, and runs a call.
/// <para>
/// Every tool that uses a change set (<see cref="AbilityAttribute.UsesChangeSet"/>) also takes an optional
/// <c>changeSetId</c>, added here so no ability declares it. A call works in the set it names, or else the only set open
/// in the conversation, which is ambient for that call only (<see cref="IChangeSetAccessor"/>). With two or more open, a
/// call must name one, so an edit never lands in a set the caller didn't mean. Every result says which set the call
/// used.
/// </para>
/// <para>
/// A call to an ability that can't be undone (<see cref="AbilityDescriptor.RequiresApproval"/>) runs only when the
/// surface says the person agreed to it. That is decided per call: in a change set the call is only recorded, which can
/// be undone, so it needs no consent; applying the set does. This is consent, not review: the person the caller acts for says yes, so a
/// model can't post by accident. How the surface asks is its own: an AI chat pauses for the person, an MCP server asks
/// through its client, an HTTP caller sends a header once the person agreed. Without it, nothing is done.
/// </para>
/// </summary>
public sealed class AbilityTool
{
    /// <summary>The argument every tool that uses a change set takes to name it.</summary>
    public const string ChangeSetIdArgument = "changeSetId";

    private const string ChangeSetIdDescription =
        "The change set to work in. Leave it out when only one change set is open in this conversation, and that one is " +
        "used. Required when two or more are open. Every result says which change set it used.";

    private readonly IAbility _ability;
    private readonly AbilityInvoker _invoker;
    private readonly AbilityToolSession _session;

    public AbilityTool(IAbility ability, AbilityInvoker invoker, AbilityToolSession session)
    {
        _ability = ability;
        _invoker = invoker;
        _session = session;
        var schema = AbilitySchemas.InputSchema(ability.Descriptor.InputType);
        JsonSchema = ability.Descriptor.UsesChangeSet ? WithChangeSetId(schema) : schema;
    }

    public AbilityDescriptor Descriptor => _ability.Descriptor;

    /// <summary>Tool names allow letters, digits, _ and -, so <c>entity.addItem</c> becomes <c>entity_addItem</c>.</summary>
    public string Name => ToToolName(_ability.Descriptor.Id);

    public string Description => _ability.Descriptor.Description;

    /// <summary>The JSON schema of the tool's input: the ability's input, plus <c>changeSetId</c> when it uses a change set.</summary>
    public JsonElement JsonSchema { get; }

    public static string ToToolName(string abilityId) => abilityId.Replace('.', '_');

    /// <summary>
    /// Runs one call with the tool's input as a JSON object. Failures come back as data, never as exceptions, so a
    /// caller can correct an input failure and stop on the others. The result is the same shape for every surface: see
    /// <see cref="AbilityToolResult"/>.
    /// </summary>
    /// <param name="arguments">The tool's input.</param>
    /// <param name="consent">
    /// The person's consent to this call. Only a call that can't be undone needs it: an ability that requires approval,
    /// run live rather than recorded in a change set. Without it, such a call does nothing and fails with
    /// <see cref="AbilityOperationStatus.ApprovalRequired"/> and an <see cref="AbilityToolResult.Approval"/> request:
    /// the surface asks the person that question and, on yes, calls again with <see cref="AbilityConsent.For"/> its id.
    /// </param>
    /// <param name="cancellationToken">Stops the call.</param>
    public async Task<AbilityToolResult> InvokeAsync(JsonObject arguments, AbilityConsent consent = default, CancellationToken cancellationToken = default)
    {
        var changeSets = _invoker.ChangeSets;
        var input = new JsonObject();
        Guid? named = null;
        foreach (var (name, value) in arguments)
        {
            if (Descriptor.UsesChangeSet && name == ChangeSetIdArgument)
            {
                if (value is null || value.GetValueKind() == JsonValueKind.Null)
                {
                    continue;
                }

                if (value.GetValueKind() != JsonValueKind.String || !Guid.TryParse(value.GetValue<string>(), out var id))
                {
                    return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.InvalidInput, "changeSetId must be a change set's id.",
                        [new Problem(ChangeSetIdArgument, "Not an id.")]), null);
                }

                named = id;
                continue;
            }

            input[name] = value?.DeepClone();
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
                return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.InvalidInput,
                    $"No change set {given}. It may have been applied or discarded.",
                    [new Problem(ChangeSetIdArgument, "Not found.")]), null);
            }

            _session.Open(given);
        }
        else if (Descriptor.UsesChangeSet && _session.OpenChangeSetIds.Count > 1)
        {
            // Guessing between two open sets could put an edit in the wrong one, so ask.
            var open = string.Join(", ", _session.OpenChangeSetIds.Select(id => $"'{changeSets.Get(id).Name}' ({id})"));
            return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.InvalidInput,
                $"{_session.OpenChangeSetIds.Count} change sets are open in this conversation: {open}. Name the one to use with changeSetId.",
                [new Problem(ChangeSetIdArgument, "Required while more than one change set is open.")]), null);
        }
        else
        {
            used = Descriptor.UsesChangeSet && _session.ChangeSetId is { } only ? changeSets.Find(only) : null;
        }

        var context = new AbilityContext(_session.UserKey);
        if (await CheckConsentAsync(input, used, consent, context, cancellationToken) is { } refused)
        {
            return refused;
        }

        AbilityResult result;
        using (changeSets.Use(used?.Id))
        {
            result = await _invoker.InvokeAsync(_ability.Descriptor.Id, input, context, cancellationToken);
        }

        var usedSet = used is null ? null : new AbilityToolChangeSet(used.Id, used.Name);
        if (result.Result is ChangeSetCreated created)
        {
            _session.Open(created.ChangeSetId);
            usedSet = new AbilityToolChangeSet(created.ChangeSetId, created.Name);
        }
        else if (used is not null && changeSets.Find(used.Id) is null)
        {
            // Applied or discarded: it leaves the conversation.
            _session.Close(used.Id);
            usedSet = usedSet! with { Open = false };
        }

        return AbilityToolResult.From(result, usedSet);
    }

    // Null when the call may run. In a set, a Write is recorded, not run (see AbilityInvoker), so only a call that runs
    // live needs consent. Applying with no set open needs none: it fails on its own.
    private async Task<AbilityToolResult?> CheckConsentAsync(JsonObject input, ChangeSet? used, AbilityConsent consent, AbilityContext context, CancellationToken cancellationToken)
    {
        var lifecycle = _ability is IChangeSetLifecycleAbility;
        var recorded = used is not null && Descriptor.Kind == AbilityKind.Write && !lifecycle;
        if (!Descriptor.RequiresApproval || recorded || (lifecycle && used is null) || consent.IsGiven)
        {
            return null;
        }

        // The approval binds to the call as the ability gets it: the input without the change set id, which is bound apart.
        var call = input.ToJsonString();
        var set = lifecycle ? used : null;
        string? why = null;
        if (consent.ApprovalId is { } id)
        {
            why = _invoker.Approvals.Consume(id, context, Descriptor.Id, call, set);
            if (why is null)
            {
                return null;
            }
        }

        var summary = set is null ? null : await _invoker.ChangeSets.SummarizeAsync(set.Id, cancellationToken);
        var request = _invoker.Approvals.Request(context, Descriptor.Id, call, set, Question(input, summary), summary);
        var message = $"{(why is null ? string.Empty : why + " ")}{Descriptor.Name} can't be undone, so the person you act for must agree to it first. Nothing was done.";
        return AbilityToolResult.From(AbilityResult.Fail(AbilityOperationStatus.ApprovalRequired, message, []),
            used is null ? null : new AbilityToolChangeSet(used.Id, used.Name)) with { Approval = request };
    }

    // What to ask the person: the call in plain words, and for a change set what applying it does.
    private string Question(JsonObject input, ChangeSetSummary? summary)
    {
        if (summary is null)
        {
            return input.Count == 0
                ? $"Allow \"{Descriptor.Name}\"? It can't be undone."
                : $"Allow \"{Descriptor.Name}\"? It can't be undone.\nWith: {input.ToJsonString()}";
        }

        var lines = new List<string> { $"{Descriptor.Name} \"{summary.Name}\"? It can't be undone." };
        lines.AddRange(summary.Entities.Select(e => $"- {e.TypeName ?? e.Entity.EntityType} \"{e.Entity.Name}\" ({e.Change})"));
        if (summary.NeedsConsent is { Count: > 0 } needs)
        {
            lines.Add("It also does these, which can't be undone either:");
            lines.AddRange(needs.Select(n => $"- {n}"));
        }

        if (summary.Rebased.Count > 0)
        {
            lines.Add($"{summary.Rebased.Count} of these were changed by someone else since; their changes are kept.");
        }

        return string.Join("\n", lines);
    }

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
}

/// <summary>
/// What a tool call returns, on every surface. The same shape as the CMS's Attempt: whether it worked and a status,
/// with the result when it worked, or a message and problems by path when it didn't. <see cref="Correctable"/> says
/// whether the caller can fix the input and try again. <see cref="ChangeSet"/> says which change set the call used.
/// </summary>
public sealed record AbilityToolResult(
    bool Success,
    AbilityOperationStatus Status,
    object? Result,
    bool? Correctable,
    string? Message,
    IReadOnlyList<Problem>? Problems,
    AbilityToolChangeSet? ChangeSet)
{
    /// <summary>
    /// When the call needs the person's consent (<see cref="AbilityOperationStatus.ApprovalRequired"/>): the question to
    /// ask them, and the id that approves this call once they agree.
    /// </summary>
    public AbilityApprovalRequest? Approval { get; init; }

    internal static AbilityToolResult From(AbilityResult result, AbilityToolChangeSet? changeSet)
        => result.Success
            ? new(true, result.Status, result.Result, null, null, null, changeSet)
            : new(false, result.Status, null, result.Correctable, result.Message, result.Problems, changeSet);

    /// <summary>The result as JSON, as a caller sees it. Only the parts that apply: a success has no problems, a failure no result.</summary>
    public JsonElement ToJson()
        => JsonSerializer.SerializeToElement(
            Success
                ? new { success = true, status = Status.ToString(), result = Result, changeSet = ChangeSet }
                : (object)new
                {
                    success = false,
                    status = Status.ToString(),
                    correctable = Correctable,
                    message = Message,
                    problems = Problems,
                    changeSet = ChangeSet,
                    approval = Approval,
                },
            AbilityJson.Options);
}

/// <summary>The change set a call worked in, as every result reports it. Open is false once applied or discarded.</summary>
public sealed record AbilityToolChangeSet(Guid Id, string Name, bool Open = true);

/// <summary>
/// Input schemas for abilities, from .NET's own JSON schema exporter, so the framework needs no AI library for them.
/// Descriptions come from <see cref="DescriptionAttribute"/> on the type, the property or its constructor parameter.
/// </summary>
public static class AbilitySchemas
{
    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = AddDescription,
    };

    // The shared options read numbers sent as text too, which would make every number "a number or a numeric string" in
    // the schema. A caller should send numbers, so the schema asks for them.
    private static readonly JsonSerializerOptions SchemaOptions = CreateSchemaOptions();

    /// <summary>The JSON schema of an input type, as a tool's input schema.</summary>
    public static JsonElement InputSchema(Type inputType)
        => JsonSerializer.SerializeToElement(JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, inputType, ExporterOptions));

    private static JsonSerializerOptions CreateSchemaOptions()
    {
        var options = new JsonSerializerOptions(AbilityJson.Options) { NumberHandling = JsonNumberHandling.Strict };
        options.MakeReadOnly();
        return options;
    }

    private static JsonNode AddDescription(JsonSchemaExporterContext context, JsonNode schema)
    {
        var attributes = context.PropertyInfo is { } property
            ? new[] { property.AttributeProvider, property.AssociatedParameter?.AttributeProvider }
            : [context.TypeInfo.Type];
        var description = attributes
            .Select(provider => provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true).OfType<DescriptionAttribute>().FirstOrDefault())
            .FirstOrDefault(attribute => attribute is not null)?.Description;
        if (description is null)
        {
            return schema;
        }

        // A schema of true (anything) has to become an object to hold a description.
        var node = schema as JsonObject ?? new JsonObject();
        node.Insert(0, "description", description);
        return node;
    }
}
