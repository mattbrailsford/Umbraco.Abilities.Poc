using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umbraco.Abilities.Consumers;

/// <summary>
/// Gives each consumer its tools: every ability, through the base policy, then the consumer's own policies and filters
/// in order, then the filters other packages added to it, then the admin's overrides. A consumer adapter reads this, and
/// only this; it doesn't filter or shape anything itself.
/// </summary>
public sealed class AbilityToolProvider(
    AbilityRegistry abilities,
    AbilityFilterRegistry filters,
    AbilityPolicyRegistry policies,
    AbilityConsumerRegistry consumers,
    AbilityOverrides? overrides = null,
    IServiceProvider? services = null)
{
    private const string OverrideSource = "override";

    /// <summary>
    /// A provider for the given abilities and consumers, with the framework's filters and policies only. For a host
    /// that isn't a site with its own registries, such as a test or a console run.
    /// </summary>
    public static AbilityToolProvider CreateDefault(AbilityRegistry abilities, params IAbilityConsumer[] consumers)
    {
        var policies = new AbilityPolicyRegistry();
        var filters = new AbilityFilterRegistry().AddFrameworkFilters(policies);
        var registry = new AbilityConsumerRegistry();
        foreach (var consumer in consumers)
        {
            registry.Add(consumer);
        }

        var provider = new AbilityToolProvider(abilities, filters, policies, registry);
        provider.Validate();
        return provider;
    }

    /// <summary>The tools a consumer offers: those not dropped.</summary>
    public IReadOnlyList<AbilityTool> GetTools(string consumerAlias)
        => Explain(consumerAlias).Where(tool => !tool.IsDropped).ToList();

    /// <summary>One tool a consumer offers, by its id, or null when it doesn't offer it.</summary>
    public AbilityTool? FindTool(string consumerAlias, string toolId)
        => GetTools(consumerAlias).FirstOrDefault(tool => tool.Id == toolId);

    /// <summary>
    /// Every tool, kept or dropped, with every filter's notes: why a tool is missing, or why it looks as it does. What
    /// an admin screen and a test read.
    /// </summary>
    public IReadOnlyList<AbilityTool> Explain(string consumerAlias)
    {
        var consumer = Consumer(consumerAlias);
        var context = new AbilityFilterContext(consumer, services);
        var admin = overrides?.For(consumerAlias) ?? new ConsumerOverride();
        IReadOnlyList<AbilityTool> tools = abilities.All
            .OrderBy(ability => ability.Descriptor.Id, StringComparer.Ordinal)
            .Select(ability => new AbilityTool(ability))
            .ToList();

        foreach (var (filter, policy) in Steps(consumer))
        {
            tools = tools
                .SelectMany(tool => tool.IsDropped
                    ? [tool]
                    : filter.Apply(tool, context).Select(changed => Kept(Stamp(changed, filter, policy), admin)))
                .ToList();
        }

        return tools.Select(tool => Excluded(tool, admin)).ToList();
    }

    /// <summary>
    /// A result of one of the consumer's tools, shaped by the same filters that shaped the tool (a result can hold a
    /// schema), so its reader gets what the tool's schemas promised.
    /// </summary>
    /// <summary>
    /// A tool's input schema once some arguments are chosen, such as the page type, for a form that shows what
    /// depends on them. The ability resolves its own input (<see cref="IDynamicInputAbility"/>); the tool's fixed arguments
    /// count as chosen. Each property the tool offers is replaced by its resolved schema, shaped by the consumer's
    /// result filters, so a person reads person hints. Null when the consumer doesn't offer the tool.
    /// </summary>
    public async Task<JsonElement?> ResolveInputSchemaAsync(string consumerAlias, string toolId, JsonObject chosen, CancellationToken cancellationToken = default)
    {
        if (FindTool(consumerAlias, toolId) is not { } tool)
        {
            return null;
        }

        if (tool.Ability is not IDynamicInputAbility dynamic)
        {
            return tool.InputSchema;
        }

        var all = (JsonObject)chosen.DeepClone();
        foreach (var (name, value) in tool.FixedArguments)
        {
            all[name] = JsonNode.Parse(value.GetRawText());
        }

        var resolved = JsonNode.Parse((await dynamic.ResolveInputSchemaAsync(all, cancellationToken)).GetRawText())!["properties"]!.AsObject();
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, _) in properties.ToList())
            {
                if (resolved[name] is { } property)
                {
                    properties[name] = property.DeepClone();
                }
                else if (tool.Ability.Descriptor.Input.DependsOn.Contains(name) || name == tool.Ability.Descriptor.Input.DependentInput)
                {
                    // Gone once resolved: nothing to choose, such as the subtype of an entity type that has none.
                    properties.Remove(name);
                }
            }
        }

        return ShapeResult(consumerAlias, tool, JsonSerializer.SerializeToElement(schema));
    }

    public JsonElement ShapeResult(string consumerAlias, AbilityTool tool, JsonElement result)
    {
        var consumer = Consumer(consumerAlias);
        var context = new AbilityFilterContext(consumer, services);
        foreach (var (filter, _) in Steps(consumer))
        {
            if (filter is IAbilityResultFilter shaper)
            {
                result = shaper.ShapeResult(tool, result, context);
            }
        }

        return result;
    }

    /// <summary>
    /// Checks every policy and consumer: each entry names a filter or a policy, no name is both, and no policy lists
    /// itself, directly or through another. Run when the site starts, so a mistake stops it rather than a call.
    /// </summary>
    public void Validate()
    {
        foreach (var alias in policies.Aliases)
        {
            if (filters.Find(alias) is not null)
            {
                throw new InvalidOperationException($"'{alias}' is both a filter and a policy. Rename one.");
            }

            _ = Resolve([alias], null, []).ToList();
        }

        foreach (var consumer in consumers.All)
        {
            _ = Steps(consumer).ToList();
        }
    }

    private IAbilityConsumer Consumer(string alias)
        => consumers.Find(alias) ?? throw new InvalidOperationException($"No consumer '{alias}'.");

    // The base policy, then the consumer's own list, then what other packages added to it.
    private IEnumerable<(IAbilityFilter Filter, string? Policy)> Steps(IAbilityConsumer consumer)
        => Resolve([AbilityPolicyRegistry.Base], null, [])
            .Concat(Resolve(consumer.Filters, null, []))
            .Concat(Resolve(consumers.AddedTo(consumer.Alias), null, []));

    private IEnumerable<(IAbilityFilter Filter, string? Policy)> Resolve(IReadOnlyList<string> entries, string? policy, List<string> path)
    {
        foreach (var entry in entries)
        {
            if (filters.Find(entry) is { } filter)
            {
                yield return (filter, policy);
            }
            else if (policies.Find(entry) is { } listed)
            {
                if (path.Contains(entry))
                {
                    throw new InvalidOperationException($"Policy '{entry}' lists itself: {string.Join(" > ", [.. path, entry])}.");
                }

                foreach (var step in Resolve(listed, entry, [.. path, entry]))
                {
                    yield return step;
                }
            }
            else
            {
                throw new InvalidOperationException($"'{entry}' is neither a filter nor a policy{(policy is null ? string.Empty : $" (listed in policy '{policy}')")}.");
            }
        }
    }

    // An admin's Include keeps a tool a consumer's filter dropped, so later filters still shape it. Never past the base
    // policy: that protects against harm.
    private static AbilityTool Kept(AbilityTool tool, ConsumerOverride admin)
        => tool.Dropped is { Policy: not AbilityPolicyRegistry.Base } dropped && admin.Includes(tool)
            ? tool with
            {
                Dropped = null,
                Notes = [.. tool.Notes, new FilterNote($"Kept: the site's override includes it, though {dropped.Filter} would leave it out.") { Filter = OverrideSource, Package = OverrideSource }],
            }
            : tool;

    private static AbilityTool Excluded(AbilityTool tool, ConsumerOverride admin)
    {
        if (tool.IsDropped || !admin.Excludes(tool))
        {
            return tool;
        }

        var note = new FilterNote("Left out by the site's override.") { Filter = OverrideSource, Package = OverrideSource };
        return tool with { Dropped = note, Notes = [.. tool.Notes, note] };
    }

    // A filter writes only a note's text; this adds where it came from.
    private static AbilityTool Stamp(AbilityTool tool, IAbilityFilter filter, string? policy)
    {
        var package = filter.GetType().Assembly.GetName().Name;
        FilterNote Sign(FilterNote note) => note.Filter is null ? note with { Filter = filter.Alias, Package = package, Policy = policy } : note;

        return tool with
        {
            Dropped = tool.Dropped is { } dropped ? Sign(dropped) : null,
            Notes = tool.Notes.Select(Sign).ToList(),
        };
    }
}
