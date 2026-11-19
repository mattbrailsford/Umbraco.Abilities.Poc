using Umbraco.Abilities.Consumers;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// Umbraco Automate, as a consumer of abilities: a person builds a step from a form, and publishing the automation is
/// their consent to it. Each step stands alone, with no conversation to keep a change set open in.
/// </summary>
public sealed class AutomateConsumer : IAbilityConsumer
{
    /// <summary>The consumer's alias.</summary>
    public const string ConsumerAlias = "automate";

    public string Alias => ConsumerAlias;

    public string Name => "Umbraco Automate";

    public string Description => "Automation steps a person builds from forms. Publishing an automation is the person's consent to its steps.";

    /// <summary>A person reads the forms, so they get person hints.</summary>
    public string Audience => AbilityAudiences.Person;

    /// <summary>No conversation and no prompt mid-run: the person agreed when they published.</summary>
    public IReadOnlyCollection<string> Capabilities { get; } = [ConsumerCapabilities.StandingConsent];

    /// <summary>The framework's policy for forms, then Automate's own filter that names each field's editor.</summary>
    public IReadOnlyList<string> Filters { get; } = [FrameworkFilters.ForForms, AutomateEditorsFilter.FilterAlias];
}
