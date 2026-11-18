
using Json.Schema;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Settings;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// One ability as an Automate action. Everything Automate shows and runs comes from the ability: its id, name and
/// description, a settings form from its input schema, and an output schema from its output type, so later steps can
/// bind to its result. One class serves every ability; nothing here is per product.
/// <para>
/// A step runs as the workspace's service account. A Write step is a change set of its own call, applied at once:
/// abilities only write through change sets. Publishing the automation is the person's consent to its steps; a Request
/// Approval step before one asks again on every run.
/// </para>
/// </summary>
public sealed class AbilityAction : IAction
{
    private readonly IAbility _ability;
    private readonly AbilityStep _step;
    private readonly EditableModelSchema _form;
    private readonly JsonSchema? _output;

    public AbilityAction(IAbility ability, AbilityStep step, AbilityActionOptions options)
    {
        _ability = ability;
        _step = step;
        Alias = options.AliasPrefix + ability.Descriptor.Id;

        // The schema a UI reads: with x-editor and x-meta, and without the change set argument: a step makes its own set.
        _form = AbilitySettingsForm.Build(AbilitySchemas.InputSchema(ability.Descriptor.InputType), options.FormatEditors);
        // Only an object schema: Automate's action list reads every output schema as an object, and one that isn't, such
        // as "true" (any value) for an ability that returns plain JSON, breaks the whole list in its editor.
        _output = ability.Descriptor.OutputType is { } output && AbilitySchemas.OutputSchema(output) is { ValueKind: System.Text.Json.JsonValueKind.Object } schema
            ? JsonSchema.FromText(schema.GetRawText())
            : null;
    }

    public string Alias { get; }

    public string Name => _ability.Descriptor.Name;

    public string? Description => _ability.Descriptor.Description;

    public string? Group => "Abilities";

    public string? Icon => _ability.Descriptor.Kind == AbilityKind.Read ? "icon-search" : "icon-wand";

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

    public Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var input = _step.Resolve((context.Settings as AbilitySettings)?.Values ?? [], context, out _);
        return _step.RunAsync(
            context,
            Name,
            writes: _ability.Descriptor is { UsesChangeSet: true, Kind: AbilityKind.Write },
            async session => StepCall.From(await _step.Runner.InvokeAsync(new AbilityTool(_ability), input, session, AbilityConsent.Given, cancellationToken)),
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
    /// Which abilities become actions. By default, all but three kinds. The change set ones: a step makes and applies
    /// its own set, so an author never opens, applies or discards one. Explaining abilities, which are for people and
    /// models. And building blocks (<see cref="AbilityDescriptor.Granular"/>), such as setting one value by its path: a
    /// person builds a step from a form, so entities get whole Create and Update actions instead (<see cref="EntityActions"/>).
    /// </summary>
    public Func<IAbility, bool> Include { get; set; } = ability =>
        !ability.Descriptor.ManagesChangeSets && ability.Descriptor.Kind != AbilityKind.Explain && !ability.Descriptor.Granular;

    /// <summary>
    /// Whether every entity type that can be created gets a Create and an Update action per subtype, such as "Create
    /// Article", with a form of that subtype's fields.
    /// </summary>
    public bool EntityActions { get; set; } = true;
}
