using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Consumers;

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

/// <summary>How a tool's call works in a change set.</summary>
public enum ChangeSetMode
{
    /// <summary>The ability never works in a change set: a read that can't depend on one, or a tool that starts one.</summary>
    None,

    /// <summary>
    /// The caller chooses the set: the one it names with <c>changeSetId</c>, or the only one open in its conversation.
    /// The default for every ability that uses a change set.
    /// </summary>
    Caller,
}

/// <summary>
/// One ability as one consumer is offered it: its name, description and schemas, shaped by that consumer's filters
/// (see <see cref="Consumers.AbilityToolProvider"/>). A consumer adapter translates it, into an AI function, an MCP tool
/// or an Automate action, and runs it with <see cref="AbilityToolRunner"/>. Nothing here runs a call.
/// <para>
/// A tool starts as its ability, as it is (<see cref="AbilityTool(IAbility)"/>). A filter changes it with <c>with</c>,
/// drops it (<see cref="Drop"/>), or puts several tools in its place, and leaves a note saying why.
/// </para>
/// </summary>
public sealed record AbilityTool
{
    /// <summary>The argument a tool in <see cref="ChangeSetMode.Caller"/> mode takes to name its change set.</summary>
    public const string ChangeSetIdArgument = "changeSetId";

    private const string ChangeSetIdDescription =
        "The change set to work in. Leave it out when only one change set is open in this conversation, and that one is " +
        "used. Required when two or more are open. Every result says which change set it used.";

    /// <summary>The ability as a tool, unshaped: its own id, name, description and schemas.</summary>
    [SetsRequiredMembers]
    public AbilityTool(IAbility ability)
    {
        var descriptor = ability.Descriptor;
        Ability = ability;
        Id = descriptor.Id;
        Title = descriptor.Name;
        Description = descriptor.Description;
        InputSchema = AbilitySchemas.InputSchema(descriptor.InputType);
        OutputSchema = descriptor.OutputType is { } output ? AbilitySchemas.OutputSchema(output) : null;
        ChangeSetMode = descriptor.UsesChangeSet ? ChangeSetMode.Caller : ChangeSetMode.None;
    }

    /// <summary>The ability this tool calls.</summary>
    public required IAbility Ability { get; init; }

    /// <summary>
    /// The tool's id: its ability's id, with a suffix when a filter split it into several, such as
    /// <c>entity.create:document</c>. Unique within one consumer's tools.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The name a caller calls it by: the id, with only the characters tool names allow (<c>entity_setValue</c>).</summary>
    public string Name => ToToolName(Id);

    /// <summary>What a person calls it, such as "Set a value".</summary>
    public required string Title { get; init; }

    /// <summary>What it does, as the consumer's reader reads it.</summary>
    public required string Description { get; init; }

    /// <summary>The JSON schema of the tool's input.</summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>The JSON schema of the tool's output, for a caller that uses one result in its next call. Null when it has none.</summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>
    /// Arguments the tool always calls its ability with, hidden from the caller. A filter that splits a tool by an
    /// argument fixes it here.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> FixedArguments { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>How a call works in a change set.</summary>
    public ChangeSetMode ChangeSetMode { get; init; }

    /// <summary>Why a filter dropped the tool. Null while it is kept. A dropped tool stays listed, so its reason can be shown.</summary>
    public FilterNote? Dropped { get; init; }

    /// <summary>What each filter did to the tool, and why, in order.</summary>
    public IReadOnlyList<FilterNote> Notes { get; init; } = [];

    /// <summary>Whether a filter dropped the tool.</summary>
    public bool IsDropped => Dropped is not null;

    /// <summary>The ability's own facts.</summary>
    public AbilityDescriptor Descriptor => Ability.Descriptor;

    /// <summary>Tool names allow letters, digits, _ and -, so <c>entity.addItem</c> becomes <c>entity_addItem</c>.</summary>
    public static string ToToolName(string id) => id.Replace('.', '_').Replace(':', '_');

    /// <summary>The tool dropped, with the reason. A filter returns this to leave the tool out.</summary>
    public AbilityTool Drop(string reason)
    {
        var note = new FilterNote(reason);
        return this with { Dropped = note, Notes = [.. Notes, note] };
    }

    /// <summary>The tool with a note of what a filter did to it, and why.</summary>
    public AbilityTool Note(string text) => this with { Notes = [.. Notes, new FilterNote(text)] };

    /// <summary>The tool with the <c>changeSetId</c> argument in its input schema, when its mode is <see cref="ChangeSetMode.Caller"/>.</summary>
    public AbilityTool WithChangeSetIdArgument()
    {
        if (ChangeSetMode != ChangeSetMode.Caller)
        {
            return this;
        }

        var node = JsonNode.Parse(InputSchema.GetRawText())!.AsObject();
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
        return this with { InputSchema = JsonSerializer.SerializeToElement(node) };
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

    /// <summary>
    /// The JSON schema of an ability's output (<see cref="AbilityDescriptor.OutputType"/>), for a caller that uses one
    /// call's result in the next, such as an Automate step's bindings. Made the same way as an input schema.
    /// </summary>
    public static JsonElement OutputSchema(Type outputType) => InputSchema(outputType);

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

        // An input property can name its editor and carry metadata too, for a form built from the schema (Automate's).
        var (editor, meta) = context.PropertyInfo is null ? (null, null) : (UiHints.EditorOf(attributes), UiHints.MetaOf(attributes));

        // A format on the property, or on its type: a type with one is written as a string, which its converter reads.
        var format = (context.PropertyInfo is null ? null : ValueFormats.OfMember(attributes)) ?? ValueFormats.Of(context.TypeInfo.Type);
        if (description is null && editor is null && meta is null && format is null)
        {
            return schema;
        }

        // A schema of true (anything) has to become an object to hold a description.
        var node = schema as JsonObject ?? new JsonObject();
        if (description is not null)
        {
            node.Insert(0, "description", description);
        }

        if (format is not null)
        {
            if (!node.ContainsKey("type"))
            {
                node["type"] = "string";
            }

            node["format"] = format;
        }

        UiHints.WriteTo(node, editor, meta);
        return node;
    }
}
