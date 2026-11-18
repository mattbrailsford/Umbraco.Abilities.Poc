namespace Umbraco.Abilities.Consumers;

/// <summary>
/// An admin's last word on one consumer's tools, by ability id. <c>*</c> at the end matches any id starting with the
/// rest, such as <c>commerce.*</c>. Read from configuration, under <c>Umbraco:Abilities:Consumers:{alias}</c>.
/// </summary>
public sealed class ConsumerOverride
{
    /// <summary>Abilities to keep, even where a consumer's filter would drop them. Never past the base policy.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Abilities to leave out, whatever the filters say.</summary>
    public List<string> Exclude { get; set; } = [];

    internal bool Includes(AbilityTool tool) => Include.Any(pattern => Matches(pattern, tool.Descriptor.Id));

    internal bool Excludes(AbilityTool tool) => Exclude.Any(pattern => Matches(pattern, tool.Descriptor.Id));

    private static bool Matches(string pattern, string id)
        => pattern.EndsWith('*')
            ? id.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : string.Equals(pattern, id, StringComparison.Ordinal);
}

/// <summary>Every consumer's override, by consumer alias.</summary>
public sealed class AbilityOverrides : Dictionary<string, ConsumerOverride>
{
    public AbilityOverrides()
        : base(StringComparer.Ordinal)
    {
    }

    /// <summary>The override for a consumer, or none.</summary>
    public ConsumerOverride For(string consumerAlias) => TryGetValue(consumerAlias, out var found) ? found : new ConsumerOverride();
}
