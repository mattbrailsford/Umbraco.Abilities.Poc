namespace Umbraco.Abilities.Consumers;

/// <summary>
/// Anything that offers abilities to someone: an AI chat, an MCP server, a workflow tool. It defines itself, and lists
/// the policies and filters that shape its tools. Nothing in the framework names a consumer: a package adds one by
/// registering it.
/// </summary>
public interface IAbilityConsumer
{
    /// <summary>Its stable name, such as <c>automate</c>. Admin overrides are keyed by it.</summary>
    string Alias { get; }

    /// <summary>What a person calls it, such as "Umbraco Automate".</summary>
    string Name { get; }

    /// <summary>What it is, for a person reading its list, such as an admin screen.</summary>
    string Description { get; }

    /// <summary>Who reads its tools' text: <see cref="AbilityAudiences.Model"/> or <see cref="AbilityAudiences.Person"/>.</summary>
    string Audience { get; }

    /// <summary>What it can do. The base policy reads these, so a consumer can't skip it by leaving it out of its list.</summary>
    ConsumerCapabilities Capabilities { get; }

    /// <summary>The policies and filters it applies, by alias, in this order, after the base policy.</summary>
    IReadOnlyList<string> Filters { get; }
}

/// <summary>A consumer defined in one line.</summary>
public sealed record AbilityConsumer(
    string Alias,
    string Name,
    string Description,
    string Audience,
    ConsumerCapabilities Capabilities,
    IReadOnlyList<string> Filters) : IAbilityConsumer;

/// <summary>
/// Who reads a consumer's text. Hints are written for an audience, never for one product, so every consumer with the
/// same audience reads the same. The list is open: a package can use a name of its own.
/// </summary>
public static class AbilityAudiences
{
    /// <summary>A language model, through an AI chat or an MCP server.</summary>
    public const string Model = "model";

    /// <summary>A person, filling in a form.</summary>
    public const string Person = "person";
}

/// <summary>What a consumer can do, as the base policy needs to know it.</summary>
[Flags]
public enum ConsumerCapabilities
{
    None = 0,

    /// <summary>Holds a conversation across calls, so a change set can stay open between them.</summary>
    Conversation = 1,

    /// <summary>Can stop and ask a person before one call, such as an AI chat's approval or an MCP client's question.</summary>
    AsksConsent = 2,

    /// <summary>A person agreed ahead of time, such as by publishing an automation.</summary>
    StandingConsent = 4,
}

/// <summary>Every consumer on the site, by alias, and the filters packages added to consumers they don't own.</summary>
public sealed class AbilityConsumerRegistry
{
    private readonly Dictionary<string, IAbilityConsumer> _consumers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _added = new(StringComparer.Ordinal);

    public AbilityConsumerRegistry Add(IAbilityConsumer consumer)
    {
        if (!_consumers.TryAdd(consumer.Alias, consumer))
        {
            throw new InvalidOperationException($"A consumer '{consumer.Alias}' is already added.");
        }

        return this;
    }

    /// <summary>
    /// Adds a filter or policy to a consumer another package owns. It runs after the consumer's own list, and can't
    /// remove or reorder anything in it. The consumer may be added later.
    /// </summary>
    public AbilityConsumerRegistry AddFilter(string consumerAlias, string entry)
    {
        if (!_added.TryGetValue(consumerAlias, out var entries))
        {
            _added[consumerAlias] = entries = [];
        }

        entries.Add(entry);
        return this;
    }

    public IAbilityConsumer? Find(string alias) => _consumers.GetValueOrDefault(alias);

    public IReadOnlyCollection<IAbilityConsumer> All => _consumers.Values;

    /// <summary>What packages added to a consumer they don't own, in the order they added it.</summary>
    public IReadOnlyList<string> AddedTo(string consumerAlias) => _added.GetValueOrDefault(consumerAlias) ?? [];
}
