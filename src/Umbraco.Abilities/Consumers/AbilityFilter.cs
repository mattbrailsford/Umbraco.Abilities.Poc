namespace Umbraco.Abilities.Consumers;

/// <summary>
/// One step in a consumer's list of tools. Despite the name, it doesn't only remove: it can drop a tool
/// (<see cref="AbilityTool.Drop"/>), change it (<c>with</c>, and <see cref="AbilityTool.Note"/>), or put several in its
/// place. Named after the MCP C# SDK's list-tools filters, which do the same.
/// <para>
/// A filter reads facts, never names: the ability's (<see cref="AbilityDescriptor"/>) and the consumer's
/// (<see cref="IAbilityConsumer"/>). It runs only on tools still kept; a dropped tool passes by it.
/// </para>
/// </summary>
public interface IAbilityFilter
{
    /// <summary>The name a policy or a consumer lists it by, such as <c>hide-building-blocks</c>. Unique on a site.</summary>
    string Alias { get; }

    /// <summary>What it does, for a person reading a consumer's list, such as an admin screen.</summary>
    string Description { get; }

    /// <summary>The tool kept as it is, changed, dropped, or several tools in its place.</summary>
    IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context);
}

/// <summary>What a filter can read: the consumer whose list it is shaping, and the site's services.</summary>
public sealed record AbilityFilterContext(IAbilityConsumer Consumer, IServiceProvider? Services = null);

/// <summary>
/// What a filter did to a tool, and why. A filter writes only the text; the provider adds which filter wrote it, the
/// package that filter comes from, and the policy that listed it, so a person can trace every change to its source.
/// </summary>
public sealed record FilterNote(string Text)
{
    /// <summary>The filter's alias.</summary>
    public string? Filter { get; init; }

    /// <summary>The package the filter comes from: the name of its assembly.</summary>
    public string? Package { get; init; }

    /// <summary>The policy that listed the filter, or null when the consumer listed it itself.</summary>
    public string? Policy { get; init; }

    public override string ToString()
        => $"{Filter} ({Package}{(Policy is null ? string.Empty : $", policy {Policy}")}): {Text}";
}

/// <summary>Every filter on the site, by alias. A package adds its own, as it adds abilities.</summary>
public sealed class AbilityFilterRegistry
{
    private readonly Dictionary<string, IAbilityFilter> _filters = new(StringComparer.Ordinal);

    public AbilityFilterRegistry Add(IAbilityFilter filter)
    {
        if (!_filters.TryAdd(filter.Alias, filter))
        {
            throw new InvalidOperationException($"A filter '{filter.Alias}' is already added.");
        }

        return this;
    }

    public IAbilityFilter? Find(string alias) => _filters.GetValueOrDefault(alias);

    public IReadOnlyCollection<IAbilityFilter> All => _filters.Values;
}
