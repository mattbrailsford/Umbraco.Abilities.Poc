using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Umbraco.Abilities.Consumers;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities.Automate;

/// <summary>Adds Umbraco Automate as a consumer of abilities, as Automate's own composer would.</summary>
public static class AutomateAbilitiesBuilderExtensions
{
    /// <summary>
    /// Adds the Automate consumer and its filter. Call it with <c>AddAbilities</c>, in any order, then
    /// <see cref="AbilityActionServiceCollectionExtensions.AddAbilityActions"/> once the builder has built.
    /// </summary>
    public static IUmbracoBuilder AddAutomateAbilities(this IUmbracoBuilder builder)
    {
        // The defaults, until AddAbilityActions registers the site's own.
        builder.Services.TryAddSingleton<AbilityActionOptions>();
        builder.AbilityConsumers().Add<AutomateConsumer>();
        builder.AbilityFilters().Add<AutomateEditorsFilter>();
        return builder;
    }
}
