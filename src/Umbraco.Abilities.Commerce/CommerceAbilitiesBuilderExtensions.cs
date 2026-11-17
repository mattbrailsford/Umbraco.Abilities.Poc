using Microsoft.Extensions.DependencyInjection;

using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace Umbraco.Abilities.Commerce;

/// <summary>Adds Umbraco Commerce's stores, currencies and discounts to the abilities, as Commerce's own composer would.</summary>
public static class CommerceAbilitiesBuilderExtensions
{
    /// <summary>
    /// Registers the store, currency and discount adapters. Call it after <c>AddAbilities</c> and <c>AddUmbracoCommerce</c>.
    /// The adapters join the registry when the site starts, as the product services are ready then.
    /// </summary>
    public static IUmbracoBuilder AddCommerceAbilities(this IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<CommerceAccess>();
        builder.Services.AddSingleton<CommerceStoreAdapter>();
        builder.Services.AddSingleton<CommerceCurrencyAdapter>();
        builder.Services.AddSingleton<CommerceDiscountAdapter>();
        builder.AddNotificationHandler<UmbracoApplicationStartingNotification, RegisterCommerceAdapters>();
        return builder;
    }

    private sealed class RegisterCommerceAdapters(EntityAdapterRegistry adapters, CommerceStoreAdapter stores, CommerceCurrencyAdapter currencies, CommerceDiscountAdapter discounts)
        : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        public void Handle(UmbracoApplicationStartingNotification notification) => adapters.Add(stores).Add(currencies).Add(discounts);
    }
}
