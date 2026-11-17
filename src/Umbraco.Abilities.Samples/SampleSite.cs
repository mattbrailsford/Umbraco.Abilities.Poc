using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Samples.Acme;
using Umbraco.Abilities.Samples.Automate;
using Umbraco.Abilities.Samples.Automate.Plugins;
using Umbraco.Abilities.Samples.Cms;
using Umbraco.Abilities.Samples.Commerce;
using Umbraco.Abilities.Samples.Commerce.Domain;
using Umbraco.Abilities.Samples.Email;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.HostedServices;

namespace Umbraco.Abilities.Samples;

/// <summary>
/// Wires the samples together the way composers would in a real site. Each block below is what one
/// package would register; none of them refers to an AI, Automate or MCP product.
/// </summary>
public sealed class SampleSite
{
    private SampleSite(IBackgroundTaskQueue? background, IEventAggregator? events)
    {
        ChangeSets = new ChangeSetService(new InMemoryChangeSetStore(), Abilities, Adapters, ItemTypes, ChangeSetAccessor, events, background, TimeSpan.Zero);
        Invoker = new AbilityInvoker(Abilities, ChangeSets);

        // Framework: the generic entity and change set abilities.
        Abilities
            .AddFrameworkAbilities(Adapters, ItemTypes, ChangeSets);

        // CMS content.
        Pages = new CmsPageAdapter(ContentDatabase, ItemTypes);
        Media = new CmsMediaAdapter(ContentDatabase, ItemTypes);
        Adapters.Add(Pages).Add(Media);

        // Commerce.
        Collections = new CollectionAdapter(CommerceDatabase, ItemTypes);
        DiscountService = new DiscountService(CommerceDatabase);
        Discounts = new DiscountAdapter(DiscountService, RuleProviders, RewardProviders, CommerceDatabase, ItemTypes);
        Adapters.Add(Collections).Add(Discounts);
        RuleProviders.AddRange([new OrderTotalRuleProvider(), new CustomerGroupRuleProvider(), new ProductCollectionRuleProvider(), new GroupRuleProvider()]);
        RewardProviders.AddRange([new PercentageOffRewardProvider(), new FreeShippingRewardProvider()]);
        var activateDiscount = new ActivateDiscountAbility(Discounts);
        Abilities.Add(activateDiscount).Add(new ListDiscountsAbility(DiscountService, Discounts, ChangeSets));
        SummerCollection = Collections.Seed("Summer collection");
        Collections.Seed("Winter coats");

        // A third-party discount rule: added to Commerce's provider collection, as its composer would. No Abilities code.
        RuleProviders.Add(new AcmeDayOfWeekRuleProvider());

        // An email package.
        var sendEmail = new SendEmailAbility(Outbox);
        Abilities.Add(sendEmail);

        // Automate: its step types are other packages' abilities.
        Workflows = new WorkflowAdapter(AutomateDatabase, ItemTypes, Triggers, Actions, ControlFlows, [sendEmail, activateDiscount]);
        Adapters.Add(Workflows);
        Triggers.AddRange([typeof(FormSubmittedTrigger), typeof(ScheduleTrigger)]);
        Actions.Add(typeof(GetContentAction));
        ControlFlows.Add(typeof(IfControlFlow));

        // Commerce's Automate package adds its trigger and action, as its composer would. No Abilities code.
        Triggers.Add(typeof(OrderCreatedTrigger));
        Actions.Add(typeof(ChangeOrderStatusAction));

        // Each product checks access with its own permission model.
        Pages.Access = Media.Access = Access;
        Collections.Access = Discounts.Access = Access;
        Workflows.Access = Access;
    }

    public SampleAccess Access { get; } = new();

    /// <summary>The change set the current call works in. A site registers one; a header middleware or a surface sets it.</summary>
    public ChangeSetAccessor ChangeSetAccessor { get; } = ChangeSetAccessor.Ambient();

    /// <summary>Commerce's discount rule and reward provider collections, which packages add to.</summary>
    public DiscountProviderCollection RuleProviders { get; } = [];

    public DiscountProviderCollection RewardProviders { get; } = [];

    /// <summary>Automate's trigger and action collections, which packages add to.</summary>
    public AutomatePluginCollection Triggers { get; } = [];

    public AutomatePluginCollection Actions { get; } = [];

    /// <summary>Automate's control flow steps, such as If.</summary>
    public AutomatePluginCollection ControlFlows { get; } = [];

    // Separate databases, as the products can each have their own connection string.
    public InMemoryDatabase ContentDatabase { get; } = new("Content");

    public InMemoryDatabase CommerceDatabase { get; } = new("Commerce");

    public InMemoryDatabase AutomateDatabase { get; } = new("Automate");

    public AbilityRegistry Abilities { get; } = new();

    public EntityAdapterRegistry Adapters { get; } = new();

    public ItemTypeRegistry ItemTypes { get; } = new();

    public ChangeSetService ChangeSets { get; }

    public AbilityInvoker Invoker { get; }

    public Outbox Outbox { get; } = new();

    public CmsPageAdapter Pages { get; }

    public CmsMediaAdapter Media { get; }

    public CollectionAdapter Collections { get; }

    public DiscountService DiscountService { get; }

    public DiscountAdapter Discounts { get; }

    public WorkflowAdapter Workflows { get; }

    public Guid SummerCollection { get; }

    public static SampleSite Create() => new(null, null);

    /// <summary>A site whose side effects with retries run on a background queue, with notifications.</summary>
    public static SampleSite Create(IBackgroundTaskQueue background, IEventAggregator events) => new(background, events);
}
