using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Samples;
using Umbraco.Abilities.Samples.Acme;
using Umbraco.Abilities.Samples.Commerce;
using Umbraco.Abilities.Samples.Commerce.Domain;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace Umbraco.Abilities.Demo;

/// <summary>
/// Adds the sample Commerce package to the demo, as its composer would: product collections, discounts with their rule
/// and reward providers, and the activate and list abilities. Its data lives in memory, so it starts empty on every run.
/// A discount's landing page is a real page on the site. No Abilities code changes for it.
/// </summary>
public sealed class DemoCommerce(
    EntityAdapterRegistry adapters,
    AbilityRegistry abilities,
    ItemTypeRegistry itemTypes,
    ChangeSetService changeSets)
    : INotificationHandler<UmbracoApplicationStartingNotification>
{
    public void Handle(UmbracoApplicationStartingNotification notification)
    {
        var database = new InMemoryDatabase("Commerce");
        var collections = new CollectionAdapter(database, itemTypes);
        var service = new DiscountService(database);
        var rules = new DiscountProviderCollection
        {
            new OrderTotalRuleProvider(), new CustomerGroupRuleProvider(), new ProductCollectionRuleProvider(), new GroupRuleProvider(),
            new AcmeDayOfWeekRuleProvider(),
        };
        var rewards = new DiscountProviderCollection { new PercentageOffRewardProvider(), new FreeShippingRewardProvider() };
        var discounts = new DiscountAdapter(service, rules, rewards, database, itemTypes);

        adapters.Add(collections).Add(discounts);
        abilities.Add(new ActivateDiscountAbility(discounts)).Add(new ListDiscountsAbility(service, discounts, changeSets));
        collections.Seed("Summer collection");
        collections.Seed("Winter coats");
    }
}
