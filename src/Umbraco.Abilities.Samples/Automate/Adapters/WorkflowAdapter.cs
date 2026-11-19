using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Samples.Automate.Plugins;

namespace Umbraco.Abilities.Samples.Automate;

/// <summary>
/// An Automate workflow as an entity. A step's exits hold the next step's key: a step with one exit has <c>next</c>, and
/// a step that names outcomes has an <c>outcomes</c> object with one exit per outcome (an If's <c>outcomes.true</c> and
/// <c>outcomes.false</c>), and no <c>next</c>: one way to route per step, so a model can't send every result where it
/// meant only one. Automate stores them as a list of connections, each with an optional outcome; this adapter converts
/// between the two on load and save. Automate's "any result" exit (an unnamed connection from a step that names
/// outcomes) has no field yet.
/// </summary>
public sealed class WorkflowAdapter(
    InMemoryDatabase database,
    ItemTypeRegistry itemTypes,
    AutomatePluginCollection triggers,
    AutomatePluginCollection actions,
    AutomatePluginCollection controlFlows,
    IReadOnlyList<IAbility> stepAbilities)
    : InMemoryModelAdapter<WorkflowEditModel>(database, itemTypes, "/umbraco/section/automate/workflow")
{
    public const string TriggerGroup = "automate.trigger";
    public const string StepGroup = "automate.step";

    /// <summary>The exit of a step with only one: a connection with no outcome in Automate.</summary>
    public const string Next = "next";

    /// <summary>The object holding a step's named exits, one per outcome.</summary>
    public const string Outcomes = "outcomes";

    private const string Connections = "connections";

    /// <summary>
    /// Triggers, actions and control flows come from Automate's plugin collections, through one source each. Steps can
    /// also be any package's abilities. Neither needs anything written per plugin.
    /// </summary>
    protected override IEnumerable<IItemTypeSource> ItemTypeSources() =>
    [
        new PluginItemTypeSource(TriggerGroup, () => triggers.Plugins.Select(p => Describe(p, withExits: false))),
        new ItemTypeSource(StepGroup, () => stepAbilities.Select(a => AbilityItemTypes.FromAbility(a, StepGroup, Exit(Next, "The step to run after this one. Omit for the last step.")))),
        new PluginItemTypeSource(StepGroup, () => actions.Plugins.Concat(controlFlows.Plugins).Select(p => Describe(p, withExits: true))),
    ];

    /// <summary>Automate's form: steps without their exits, and a list of connections between steps.</summary>
    public override Task SaveModelAsync(WorkflowEditModel workflow, CancellationToken cancellationToken = default)
    {
        var connections = new List<StepConnection>();
        foreach (var step in workflow.Steps)
        {
            if (step.Fields.Remove(Outcomes, out var outcomes) && outcomes is JsonObject named)
            {
                connections.AddRange(named.Where(o => o.Value is not null).Select(o => new StepConnection(step.Key, KeyOf(o.Value), o.Key)));
            }

            if (step.Fields.Remove(Next, out var next) && next is not null)
            {
                connections.Add(new StepConnection(step.Key, KeyOf(next), null));
            }
        }

        var stored = ToEntity(workflow).Fields;
        stored[Connections] = JsonSerializer.SerializeToNode(connections, AbilityJson.Options);
        Database.Put(EntityType, workflow.Key, stored);
        return Task.CompletedTask;
    }

    /// <summary>Puts each connection back on its step, as the exit its outcome names.</summary>
    public override Task<WorkflowEditModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
    {
        if (Database.Get(EntityType, key) is not { } row)
        {
            return Task.FromResult<WorkflowEditModel?>(null);
        }

        var fields = (JsonObject)row.Root.DeepClone();
        var connections = fields[Connections]?.Deserialize<List<StepConnection>>(AbilityJson.Options) ?? [];
        fields.Remove(Connections);
        var workflow = ToModel(new Entity { EntityType = EntityType, Key = key, Version = row.Version, Fields = fields });
        foreach (var connection in connections)
        {
            var step = workflow.Steps.Single(s => s.Key == connection.SourceStepId);
            var target = JsonValue.Create(connection.TargetStepId.ToString());
            if (connection.Outcome is { } outcome)
            {
                var named = step.Fields.GetValueOrDefault(Outcomes) as JsonObject ?? [];
                named[outcome] = target;
                step.Fields[Outcomes] = named;
            }
            else
            {
                step.Fields[Next] = target;
            }
        }

        return Task.FromResult<WorkflowEditModel?>(workflow);
    }

    private static Guid KeyOf(JsonNode? exit) => Guid.Parse(exit!.GetValue<string>());

    /// <summary>
    /// An Automate plugin, as the framework describes a plugin: the only thing Automate writes per seam. Its required
    /// sections decide who is offered it, and a step's exits come from the outcomes it names.
    /// </summary>
    private PluginDescriptor Describe(AutomatePlugin plugin, bool withExits) => new(
        plugin.Alias,
        plugin.Name,
        plugin.Description ?? plugin.Name,
        plugin.Fields.Select(f => new SettingDescriptor(f.Key, f.Field.Label, f.Field.Description, f.PropertyType, f.Rules, f.Field.EditorUiAlias, f.Field.SortOrder, f.Field.SupportsBindings)).ToList())
    {
        ExtraFields = withExits ? Exits(plugin.Outcomes) : [],
        AvailableTo = (user, _) => ValueTask.FromResult(Access is not { } access || plugin.RequiredSections.All(section => access.HasSection(user, section))),
    };

    /// <summary>A step's exits: <c>next</c>, or an <c>outcomes</c> object when the step names outcomes.</summary>
    public static IReadOnlyList<FieldDefinition> Exits(IReadOnlyList<StepOutcome> outcomes)
        => outcomes.Count == 0
            ? [Exit(Next, "The step to run after this one. Omit for the last step.")]
            :
            [
                FieldDefinition.ObjectOf(Outcomes, "Where to go next, by how this step finishes. Omit an outcome to end the run there.",
                    new ObjectSchema(outcomes.Select(o => Exit(o.Key, string.Join(" ", new[]
                    {
                        $"The step to run when this step finishes with '{o.Key}'.",
                        ItemTypeFields.Text(o.Description),
                    }.OfType<string>()))).ToList())),
            ];

    private static FieldDefinition Exit(string alias, string description)
        => new(alias, description, FieldType.ItemReference) { ReferencesItemIn = "steps" };
}
