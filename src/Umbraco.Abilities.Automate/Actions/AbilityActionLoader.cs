using Umbraco.Abilities.Consumers;
using Umbraco.Automate.Core.Actions;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// Makes the site's Automate actions, at the time it's asked: an action per tool the Automate consumer offers. The
/// consumer's filters decide them: whole operations only, each Write a change set of its own, and a Create and an Update
/// per entity type, in which a person picks the page type.
/// </summary>
public sealed class AbilityActionLoader(AbilityStep step, AbilityToolProvider tools, AbilityActionOptions options)
{
    public IEnumerable<IAction> CreateActions()
        => tools.GetTools(AutomateConsumer.ConsumerAlias).Select(tool => (IAction)new AbilityAction(tool, step, tools, options));
}
