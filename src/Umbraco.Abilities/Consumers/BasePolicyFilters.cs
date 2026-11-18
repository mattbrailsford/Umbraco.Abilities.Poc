namespace Umbraco.Abilities.Consumers;

/// <summary>
/// Drops an ability that can't be undone where nobody can agree to it: the consumer can't ask a person before a call,
/// and no person agreed ahead of time. In the base policy.
/// </summary>
public sealed class ConsentFilter : IAbilityFilter
{
    public string Alias => "consent";

    public string Description => "Leaves out what can't be undone where nobody can agree to it first.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        var canAgree = context.Consumer.Capabilities.HasFlag(ConsumerCapabilities.AsksConsent)
            || context.Consumer.Capabilities.HasFlag(ConsumerCapabilities.StandingConsent);
        yield return tool.Descriptor.RequiresApproval && !canAgree
            ? tool.Drop($"It can't be undone, and {context.Consumer.Name} can't ask a person to agree to it, nor did one agree ahead of time.")
            : tool;
    }
}

/// <summary>
/// Drops an ability that creates, applies or discards change sets where no conversation keeps a set open between calls.
/// There, the consumer makes and applies a set per call itself. In the base policy.
/// </summary>
public sealed class ChangeSetLifecycleFilter : IAbilityFilter
{
    public string Alias => "change-set-lifecycle";

    public string Description => "Leaves out the change set tools where no conversation keeps a set open.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        yield return tool.Descriptor.ManagesChangeSets && !context.Consumer.Capabilities.HasFlag(ConsumerCapabilities.Conversation)
            ? tool.Drop($"It manages change sets, and {context.Consumer.Name} keeps no conversation to hold one open in.")
            : tool;
    }
}

public static class FrameworkFilters
{
    /// <summary>Adds the framework's filters, and puts the ones that protect against harm in the base policy.</summary>
    public static AbilityFilterRegistry AddFrameworkFilters(this AbilityFilterRegistry filters, AbilityPolicyRegistry policies)
    {
        IAbilityFilter[] basePolicy = [new ConsentFilter(), new ChangeSetLifecycleFilter()];
        foreach (var filter in basePolicy)
        {
            filters.Add(filter);
            policies.AddToBasePolicy(filter.Alias);
        }

        return filters;
    }
}
