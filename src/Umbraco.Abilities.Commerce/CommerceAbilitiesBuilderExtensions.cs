using Microsoft.Extensions.DependencyInjection;

using Umbraco.Abilities;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities.Commerce;

/// <summary>
/// Adds Umbraco Commerce's stores, currencies and discounts to the abilities, and the tasks Commerce offers on them, as
/// Commerce's own composer would.
/// </summary>
public static class CommerceAbilitiesBuilderExtensions
{
    /// <summary>
    /// Adds the store, currency and discount adapters to the site's entity adapters, and Commerce's task abilities:
    /// add a discount code, and switch a discount on or off. Call it with <c>AddAbilities</c> and
    /// <c>AddUmbracoCommerce</c>, in any order.
    /// </summary>
    public static IUmbracoBuilder AddCommerceAbilities(this IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<CommerceAccess>();
        builder.EntityAdapters()
            .Add<CommerceStoreAdapter>()
            .Add<CommerceCurrencyAdapter>()
            .Add<CommerceDiscountAdapter>();
        builder.Abilities()
            .Add<AddDiscountCodeAbility>()
            .Add<SetDiscountActiveAbility>();
        return builder;
    }
}
