using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;
using Umbraco.Abilities.Consumers;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Settings;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// One of the Automate consumer's tools as an Automate action. Everything Automate shows and runs comes from the tool:
/// its id, name and description, a settings form from its input schema, and an output schema from its output, so later
/// steps can bind to its result. Which tools there are, and their shape, are the consumer's filters'
/// (<see cref="AutomateConsumer"/>). One class serves every ability; nothing here is per product.
/// <para>
/// A step runs as the workspace's service account. A Write is a change set of its own, applied at once (the tool's
/// <see cref="ChangeSetMode.PerCall"/>). Publishing the automation is the person's consent to its steps; a Request
/// Approval step before one asks again on every run.
/// </para>
/// <para>
/// A tool split by a choice, such as "Create Document", has the choice fixed. One with a dependent input, such as the
/// page type's fields, gets its form each time Automate asks, so a page type added later is offered; the fields are
/// drawn by the fields editor (see <see cref="AutomateEditorsFilter.DependentInputEditor"/>).
/// </para>
/// </summary>
public sealed class AbilityAction : IAction
{
    private readonly AbilityTool _tool;
    private readonly AbilityStep _step;
    private readonly AbilityToolProvider _tools;
    private readonly AbilityActionOptions _options;
    private readonly EditableModelSchema _form;
    private readonly JsonSchema? _output;

    public AbilityAction(AbilityTool tool, AbilityStep step, AbilityToolProvider tools, AbilityActionOptions options)
    {
        _tool = tool;
        _step = step;
        _tools = tools;
        _options = options;

        // A split tool's id has the choice after a colon (entity.create:document); an alias keeps to dots.
        Alias = options.AliasPrefix + tool.Id.Replace(':', '.');
        _form = AbilitySettingsForm.Build(tool.InputSchema, options.FormatEditors);

        // Only an object schema: Automate's action list reads every output schema as an object, and one that isn't, such
        // as "true" (any value) for an ability that returns plain JSON, breaks the whole list in its editor.
        _output = tool.OutputSchema is { ValueKind: System.Text.Json.JsonValueKind.Object } schema
            ? JsonSchema.FromText(schema.GetRawText())
            : null;
    }

    public string Alias { get; }

    public string Name => _tool.Title;

    public string? Description => _tool.Description;

    public string? Group => "Abilities";

    public string? Icon => _tool.Descriptor.Kind == AbilityKind.Read ? "icon-search" : "icon-wand";

    public string? ConnectionTypeAlias => null;

    // Who may use the ability is checked when it runs, as the service account: the ability's own permission checks.
    public IReadOnlyList<string> RequiredSections => [];

    public IReadOnlyList<string> RequiredPermissions => [];

    // FUDGE (SettingsType): Automate resolves a step's settings, and passes them to the action, only when the action
    // has a settings type. The ability's input is JSON, so the type is a holder for it.
    public Type? SettingsType => typeof(AbilitySettings);

    // A dynamic input is resolved each time: its choices (such as page types) are the site's now.
    public EditableModelSchema? GetSettingsSchema()
        => _tool.Ability is IDynamicInputAbility
            && _tools.ResolveInputSchemaAsync(AutomateConsumer.ConsumerAlias, _tool.Id, []).GetAwaiter().GetResult() is { } schema
            ? AbilitySettingsForm.Build(schema, _options.FormatEditors)
            : _form;

    public object? ResolveSettings(Dictionary<string, object?> settings) => new AbilitySettings { Values = AbilityStep.ToJson(settings) };

    // The output's shape is a schema, not a C# type Automate can read.
    public Type? OutputType => null;

    public JsonSchema? GetOutputSchema() => _output;

    public bool HasDynamicOutputSchema => false;

    public Task<JsonSchema?> GetOutputSchemaAsync(Dictionary<string, object?>? settings, CancellationToken cancellationToken = default)
        => Task.FromResult(_output);

    // The runner makes, applies or discards the step's change set: the tool says how (its ChangeSetMode).
    public Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = (context.Settings as AbilitySettings)?.Values ?? [];
        var input = _step.Resolve(settings, context, out _);
        return _step.RunAsync(
            context,
            Name,
            writes: false,
            async session => StepCall.From(await _step.Runner.InvokeAsync(
                _tool, await FromFormAsync(settings, input, cancellationToken), session, AbilityConsent.Given, cancellationToken, AbilityStep.ChangeSetName(context))),
            output: null,
            cancellationToken);
    }

    // The form's values as the ability takes them. A picker gives a key as a list of picked items, and a dropdown its
    // choice as a list of one. The dependent input's
    // values come from each field's editor: empty ones are left out (on an update they stay as they are), and the
    // ability reads the rest. A value bound to an earlier step's output is a value already.
    private async Task<JsonObject> FromFormAsync(JsonObject settings, JsonObject input, CancellationToken cancellationToken)
    {
        var properties = _tool.InputSchema.TryGetProperty("properties", out var list) ? list : default;
        foreach (var (name, value) in input.ToList())
        {
            if (value is JsonArray or JsonObject && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var property)
                && property.TryGetProperty("format", out var format) && format.GetString() == "uuid")
            {
                input[name] = KeyOf(value);
            }
            else if (value is JsonArray { Count: 1 } one && one[0] is JsonValue text && text.GetValueKind() == JsonValueKind.String
                && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var choice) && IsText(choice))
            {
                input[name] = text.DeepClone();
            }
        }

        if (_tool.Descriptor.Input.DependentInput is not { } dependent || _tool.Ability is not IDynamicInputAbility dynamic || input[dependent] is not JsonObject values)
        {
            return input;
        }

        var given = new JsonObject(values.Where(v => !IsEmpty(v.Value)).Select(v => KeyValuePair.Create(v.Key, v.Value?.DeepClone())));
        var bound = (settings[dependent] as JsonObject ?? [])
            .Where(v => v.Value?.ToJsonString().Contains("${", StringComparison.Ordinal) == true)
            .Select(v => v.Key)
            .ToHashSet(StringComparer.Ordinal);
        var chosen = new JsonObject(input.Where(i => i.Key != dependent).Select(i => KeyValuePair.Create(i.Key, i.Value?.DeepClone())));
        foreach (var (name, value) in _tool.FixedArguments)
        {
            chosen[name] = JsonNode.Parse(value.GetRawText());
        }

        input[dependent] = await dynamic.FromEditorValuesAsync(chosen, given, bound, cancellationToken);
        return input;
    }

    // A picker may give a key as text, or as a list of one picked item.
    private static JsonNode? KeyOf(JsonNode? value) => value switch
    {
        JsonArray { Count: > 0 } picked => KeyOf(picked[0]),
        JsonArray => null,
        JsonObject item => (item["unique"] ?? item["key"] ?? item["id"])?.DeepClone(),
        _ => value?.DeepClone(),
    };

    // A property that takes text: "string", or a list of types with "string" in it.
    private static bool IsText(JsonElement property)
        => property.TryGetProperty("type", out var type)
            && (type.ValueKind == JsonValueKind.String ? type.GetString() == "string" : type.EnumerateArray().Any(t => t.GetString() == "string"));

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonValue text when text.GetValueKind() == JsonValueKind.String => text.GetValue<string>().Length == 0,
        JsonArray list => list.Count == 0,
        _ => false,
    };
}

/// <summary>How the loader makes abilities into Automate actions.</summary>
public sealed class AbilityActionOptions
{
    /// <summary>Put before every ability's id, so its alias can't clash with Automate's own actions.</summary>
    public string AliasPrefix { get; set; } = "abilities.";

    /// <summary>
    /// The editor for each value format, such as <c>date</c>. Every field of that format gets it, in any ability. A
    /// package adds its own format's editor here.
    /// </summary>
    public Dictionary<string, string> FormatEditors { get; } = new(StringComparer.Ordinal)
    {
        ["date"] = "Umb.PropertyEditorUi.DatePicker",
        ["date-time"] = "Umb.PropertyEditorUi.DatePicker",
    };

}
