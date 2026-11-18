
using Json.Schema;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Settings;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// One of the Automate consumer's tools as an Automate action. Everything Automate shows and runs comes from the tool:
/// its id, name and description, a settings form from its input schema, and an output schema from its output, so later
/// steps can bind to its result. Which tools there are, and their shape, are the consumer's filters'
/// (<see cref="AbilityActionLoader.Consumer"/>). One class serves every ability; nothing here is per product.
/// <para>
/// A step runs as the workspace's service account. A Write is a change set of its own, applied at once (the tool's
/// <see cref="ChangeSetMode.PerCall"/>). Publishing the automation is the person's consent to its steps; a Request
/// Approval step before one asks again on every run.
/// </para>
/// </summary>
public sealed class AbilityAction : IAction
{
    private readonly AbilityTool _tool;
    private readonly AbilityStep _step;
    private readonly EditableModelSchema _form;
    private readonly JsonSchema? _output;

    public AbilityAction(AbilityTool tool, AbilityStep step, AbilityActionOptions options)
    {
        _tool = tool;
        _step = step;
        Alias = options.AliasPrefix + tool.Id;
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

    public EditableModelSchema? GetSettingsSchema() => _form;

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
        var input = _step.Resolve((context.Settings as AbilitySettings)?.Values ?? [], context, out _);
        return _step.RunAsync(
            context,
            Name,
            writes: false,
            async session => StepCall.From(await _step.Runner.InvokeAsync(
                _tool, input, session, AbilityConsent.Given, cancellationToken, AbilityStep.ChangeSetName(context))),
            output: null,
            cancellationToken);
    }
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

    /// <summary>
    /// Whether every entity type that can be created gets a Create and an Update action per subtype, such as "Create
    /// Article", with a form of that subtype's fields.
    /// </summary>
    public bool EntityActions { get; set; } = true;
}
