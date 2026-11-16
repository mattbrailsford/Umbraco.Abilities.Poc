using System.Text.Json.Nodes;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Samples.Automate.Plugins;

namespace Umbraco.Abilities.Samples.Automate;

/// <summary>
/// An Automate workflow as an entity: one trigger, and steps whose item types are abilities and Automate's actions and
/// control flows. A step's exits hold the next step's key: a step with one exit has <c>next</c>, and a step that names
/// outcomes has an <c>outcomes</c> object with one exit per outcome (an If's <c>outcomes.true</c> and
/// <c>outcomes.false</c>), and no <c>next</c>: one way to route per step, so a model can't send every result where it
/// meant only one. Automate stores them as a list of connections, each with an optional outcome; this adapter converts
/// between the two on load and save. Automate's "any result" exit (an unnamed connection from a step that names
/// outcomes) has no field yet.
/// </summary>
public sealed class WorkflowAdapter(InMemoryDatabase database)
    : InMemoryEntityAdapter(database, "umbraco-automate-workflow", "name", "/umbraco/section/automate/workflow")
{
    public const string TriggerGroup = "automate.trigger";
    public const string StepGroup = "automate.step";

    /// <summary>The exit of a step with only one: a connection with no outcome in Automate.</summary>
    public const string Next = "next";

    /// <summary>The object holding a step's named exits, one per outcome.</summary>
    public const string Outcomes = "outcomes";

    /// <summary>
    /// Triggers, actions and control flows come from Automate's plugin collections, through one source each. Steps can
    /// also be any package's abilities. Neither needs anything written per plugin.
    /// </summary>
    public static void RegisterItemTypes(
        ItemTypeRegistry itemTypes,
        IEnumerable<IAbility> stepAbilities,
        AutomatePluginCollection triggers,
        AutomatePluginCollection actions,
        AutomatePluginCollection controlFlows,
        Func<Guid, string, bool> hasSection)
    {
        itemTypes.AddSource(new PluginItemTypeSource(TriggerGroup, () => triggers.Plugins.Select(p => Describe(p, hasSection, withExits: false))));
        itemTypes.AddSource(new PluginItemTypeSource(StepGroup, () => actions.Plugins.Select(p => Describe(p, hasSection, withExits: true))));
        itemTypes.AddSource(new PluginItemTypeSource(StepGroup, () => controlFlows.Plugins.Select(p => Describe(p, hasSection, withExits: true))));

        foreach (var ability in stepAbilities)
        {
            itemTypes.Add(AbilityItemTypes.FromAbility(ability, StepGroup, Exit(Next, "The step to run after this one. Omit for the last step.")));
        }
    }

    /// <summary>
    /// An Automate plugin, as the framework describes a plugin: the only thing Automate writes per seam. Its required
    /// sections decide who is offered it, and a step's exits come from the outcomes it names.
    /// </summary>
    private static PluginDescriptor Describe(AutomatePlugin plugin, Func<Guid, string, bool> hasSection, bool withExits) => new(
        plugin.Alias,
        plugin.Name,
        plugin.Description ?? plugin.Name,
        plugin.Fields.Select(f => new SettingDescriptor(f.Key, f.Field.Label, f.Field.Description, f.PropertyType, f.Rules, f.Field.EditorUiAlias, f.Field.SortOrder, f.Field.SupportsBindings)).ToList())
    {
        ExtraFields = withExits ? Exits(plugin.Outcomes) : [],
        AvailableTo = user => plugin.RequiredSections.All(section => hasSection(user, section)),
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

    public override Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default)
        => Task.FromResult(new EntitySchema(EntityType, "Workflow", "An automation: a trigger, then steps.", ObjectSchema
            .Of(
                new FieldDefinition("name", "Workflow name.", FieldType.String, Required: true),
                new FieldDefinition("enabled", "Whether the workflow runs.", FieldType.Boolean),
                FieldDefinition.ItemsOf("trigger", "What starts the workflow.", TriggerGroup) with { Min = 1, Max = 1 },
                FieldDefinition.ItemsOf("steps", "The steps. Each step names the step to run next, once per exit.", StepGroup) with { Min = 1 })));

    /// <summary>Automate's form: steps without their exits, and a list of connections between steps.</summary>
    public override Task<EntitySummary> SaveAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var stored = (JsonObject)entity.Fields.DeepClone();
        var connections = new JsonArray();
        foreach (var step in (stored["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var exits = (step[Outcomes] as JsonObject ?? []).Select(o => (Outcome: (string?)o.Key, Target: o.Value)).ToList();
            exits.Add((null, step[Next]));
            foreach (var (outcome, target) in exits.Where(e => e.Target is not null))
            {
                connections.Add(new JsonObject
                {
                    ["sourceStepId"] = step["key"]!.DeepClone(),
                    ["targetStepId"] = target!.DeepClone(),
                    ["outcome"] = outcome,
                });
            }

            step.Remove(Outcomes);
            step.Remove(Next);
        }

        stored["connections"] = connections;
        Database.Put(EntityType, entity.Key, stored);
        return Task.FromResult(ToSummary(entity));
    }

    /// <summary>Puts each connection back on its step, as the exit its outcome names.</summary>
    public override Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default)
    {
        if (Database.Get(EntityType, key) is not { } row)
        {
            return Task.FromResult<Entity?>(null);
        }

        var fields = (JsonObject)row.Root.DeepClone();
        var steps = (fields["steps"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(s => s["key"]!.GetValue<string>());
        foreach (var connection in (fields["connections"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var source = steps[connection["sourceStepId"]!.GetValue<string>()];
            var target = connection["targetStepId"]!.DeepClone();
            if (connection["outcome"]?.GetValue<string>() is { } outcome)
            {
                var named = source[Outcomes] as JsonObject ?? (JsonObject)(source[Outcomes] = new JsonObject());
                named[outcome] = target;
            }
            else
            {
                source[Next] = target;
            }
        }

        fields.Remove("connections");
        return Task.FromResult<Entity?>(new Entity { EntityType = EntityType, Key = key, Version = row.Version, Fields = fields });
    }
}
