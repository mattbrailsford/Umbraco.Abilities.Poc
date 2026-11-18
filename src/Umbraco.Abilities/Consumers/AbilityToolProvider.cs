namespace Umbraco.Abilities.Consumers;

/// <summary>
/// Gives each consumer its tools: every ability, through the base policy, then the consumer's own policies and filters
/// in order, then the filters other packages added to it. A consumer adapter reads this, and only this; it doesn't
/// filter or shape anything itself.
/// </summary>
public sealed class AbilityToolProvider(
    AbilityRegistry abilities,
    AbilityFilterRegistry filters,
    AbilityPolicyRegistry policies,
    AbilityConsumerRegistry consumers,
    IServiceProvider? services = null)
{
    /// <summary>The tools a consumer offers: those no filter dropped.</summary>
    public IReadOnlyList<AbilityTool> GetTools(string consumerAlias)
        => Explain(consumerAlias).Where(tool => !tool.IsDropped).ToList();

    /// <summary>
    /// Every tool, kept or dropped, with every filter's notes: why a tool is missing, or why it looks as it does. What
    /// an admin screen and a test read.
    /// </summary>
    public IReadOnlyList<AbilityTool> Explain(string consumerAlias)
    {
        var consumer = consumers.Find(consumerAlias) ?? throw new InvalidOperationException($"No consumer '{consumerAlias}'.");
        var context = new AbilityFilterContext(consumer, services);
        IReadOnlyList<AbilityTool> tools = abilities.All
            .OrderBy(ability => ability.Descriptor.Id, StringComparer.Ordinal)
            .Select(ability => new AbilityTool(ability))
            .ToList();

        foreach (var (filter, policy) in Steps(consumer))
        {
            tools = tools
                .SelectMany(tool => tool.IsDropped ? [tool] : filter.Apply(tool, context).Select(changed => Stamp(changed, filter, policy)))
                .ToList();
        }

        return tools;
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
