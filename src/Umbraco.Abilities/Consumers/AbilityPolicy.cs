namespace Umbraco.Abilities.Consumers;

/// <summary>
/// Policies: filters grouped under one name, applied in order. A consumer applies a policy by name, as ASP.NET Core
/// applies a CORS or output caching policy. An entry in a policy is a filter's alias or another policy's.
/// <para>
/// The <see cref="Base"/> policy runs first for every consumer, before its own list, and nothing turns it off. It holds
/// the filters that protect against harm, not clutter.
/// </para>
/// </summary>
public sealed class AbilityPolicyRegistry
{
    /// <summary>The alias of the base policy.</summary>
    public const string Base = "base";

    private readonly Dictionary<string, List<string>> _policies = new(StringComparer.Ordinal) { [Base] = [] };

    /// <summary>Adds a policy: its entries, filter or policy aliases, in the order they run.</summary>
    public AbilityPolicyRegistry Add(string alias, params string[] entries)
    {
        if (!_policies.TryAdd(alias, [.. entries]))
        {
            throw new InvalidOperationException($"A policy '{alias}' is already added.");
        }

        return this;
    }

    /// <summary>
    /// Adds an entry at the end of another package's policy. Every consumer that applies the policy gets it: a new kind
    /// of tool to leave out, or a new keyword to strip, is handled once.
    /// </summary>
    public AbilityPolicyRegistry AddToPolicy(string alias, string entry)
    {
        if (!_policies.TryGetValue(alias, out var entries))
        {
            throw new InvalidOperationException($"No policy '{alias}' to add '{entry}' to.");
        }

        entries.Add(entry);
        return this;
    }

    /// <summary>Adds a filter to the base policy, which runs for every consumer. Nothing can take it out again.</summary>
    public AbilityPolicyRegistry AddToBasePolicy(string entry) => AddToPolicy(Base, entry);

    /// <summary>A policy's entries, or null when there is no such policy.</summary>
    public IReadOnlyList<string>? Find(string alias) => _policies.GetValueOrDefault(alias);

    public IReadOnlyCollection<string> Aliases => _policies.Keys;
}
