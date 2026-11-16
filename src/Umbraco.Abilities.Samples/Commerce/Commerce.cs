using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Abilities.Samples.Commerce.Domain;

namespace Umbraco.Abilities.Samples.Commerce;

// ── The editing contracts: plain C# classes, separate from Commerce's domain model (Domain.cs). Everything the
// AI and change sets need is derived from them; the adapter maps them to and from the domain. ─────────────

[Entity("umbraco-commerce-collection", "Collection", Description = "A product collection.")]
public sealed class Collection
{
    [Key]
    public Guid Key { get; set; }

    [EntityVersion]
    public long? Version { get; set; }

    [EntityName, Required, Description("Collection name.")]
    public string? Name { get; set; }
}

[Entity("umbraco-commerce-discount", "Discount", Description = "A discount applied when all its rules match.")]
public sealed class DiscountEditModel
{
    [Key]
    public Guid Key { get; set; }

    [EntityVersion]
    public long? Version { get; set; }

    [EntityName, Required, Description("Name shown to editors.")]
    public string? Name { get; set; }

    [Description("Optional coupon code. Unique.")]
    public string? Code { get; set; }

    [Description("First day the discount applies.")]
    public DateOnly? StartDate { get; set; }

    [Description("Last day the discount applies.")]
    public DateOnly? EndDate { get; set; }

    [EntityKey("document"), Description("Optional page that promotes the discount.")]
    public Guid? LandingPage { get; set; }

    [Description("Whether the discount is live. Use commerce.activateDiscount to switch it on.")]
    public bool? Active { get; set; }

    [Items(DiscountAdapter.RuleGroup, Min = 1), Description("All rules must match for the discount to apply.")]
    public List<DynamicItem> Rules { get; set; } = [];

    [Items(DiscountAdapter.RewardGroup, Min = 1, Max = 3), Description("What the customer gets.")]
    public List<DynamicItem> Rewards { get; set; } = [];
}

// Rules and rewards are a plugin seam: Commerce and other packages register providers (Providers.cs), so their item
// types come from the provider collections at run time (a PluginItemTypeSource per seam, below), not from classes here.

// ── The adapters: load, save, and the rules attributes can't express. ─────────────────────────────────

public sealed class CollectionAdapter(InMemoryDatabase database, ItemTypeRegistry itemTypes)
    : InMemoryModelAdapter<Collection>(database, itemTypes, "/umbraco/section/commerce/collection")
{
    public override bool CanCreate => false;

    public Guid Seed(string name)
    {
        var collection = new Collection { Key = Guid.NewGuid(), Name = name };
        SaveModelAsync(collection).GetAwaiter().GetResult();
        return collection.Key;
    }
}

/// <summary>
/// Maps Commerce's own discount model to and from its editing contract. This mapping is the part a package writes by
/// hand; the schema, schemas, paths, validation and change sets come from <see cref="DiscountEditModel"/>.
/// </summary>
public sealed class DiscountAdapter(
    DiscountService discounts,
    DiscountProviderCollection ruleProviders,
    DiscountProviderCollection rewardProviders,
    InMemoryDatabase unitOfWork,
    ItemTypeRegistry itemTypes)
    : EntityAdapterBase<DiscountEditModel>(itemTypes, unitOfWork), IAuthorizingEntityAdapter
{
    /// <summary>Real Commerce would check the Commerce section and the user's access to the discount's store.</summary>
    public SampleAccess? Access { get; set; }

    public Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(Access?.Authorize(EntityType, request));

    public const string RuleGroup = "commerce.discount.rule";
    public const string RewardGroup = "commerce.discount.reward";

    private const string Children = "children";

    /// <summary>Commerce's table from an editor UI alias to what it picks. Editors not listed stay plain values.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Editors = new Dictionary<string, string[]>
    {
        ["Uc.PropertyEditorUi.CollectionPicker"] = ["umbraco-commerce-collection"],
    };

    /// <summary>One source per seam. Providers registered later, by any package, show up with no change here.</summary>
    public static void RegisterItemTypes(ItemTypeRegistry itemTypes, DiscountProviderCollection rules, DiscountProviderCollection rewards, Func<Guid, bool> availableTo) => itemTypes
        .AddSource(new PluginItemTypeSource(RuleGroup, () => rules.Select(p => Describe(p) with { AvailableTo = availableTo }), Editors))
        .AddSource(new PluginItemTypeSource(RewardGroup, () => rewards.Select(p => Describe(p) with { AvailableTo = availableTo }), Editors));

    /// <summary>
    /// A Commerce provider, as the framework describes a plugin: the only thing Commerce writes per seam. A group rule
    /// holds child rules, as an items field.
    /// </summary>
    private static PluginDescriptor Describe(DiscountProviderBase provider) => new(
        provider.Alias,
        provider.Label,
        provider.Description,
        provider.SettingDefinitions.Select(s => new SettingDescriptor(s.Key, s.Label, s.Description, s.PropertyType, s.ValidationRules, s.EditorUiAlias, s.SortOrder)).ToList())
    {
        ExtraFields = provider.HasChildren ? [FieldDefinition.ItemsOf(Children, "The rules in this group.", RuleGroup) with { Min = 1 }] : [],
    };

    public override Task<DiscountEditModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
        => Task.FromResult(discounts.GetDiscount(key) is { } discount ? ToEditModel(discount) : null);

    public override Task SaveModelAsync(DiscountEditModel model, CancellationToken cancellationToken = default)
    {
        var discount = discounts.GetDiscount(model.Key)?.AsWritable() ?? discounts.CreateDiscount(model.Key, model.Name!);
        discount
            .SetName(model.Name!)
            .SetCode(model.Code)
            .SetDates(ToUtc(model.StartDate), ToUtc(model.EndDate))
            .SetActive(model.Active ?? false)
            .SetLandingPage(model.LandingPage)
            .SetRules(model.Rules.Select(ToRuleConfig))
            .SetRewards(model.Rewards.Select(r => new DiscountRewardConfig(r.Type, PluginSettings.ToSettings(r))));
        discounts.SaveDiscount(discount);
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<DiscountEditModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DiscountEditModel>>(discounts.GetDiscounts()
            .Where(d => d.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(ToEditModel)
            .ToList());

    protected override Task<IReadOnlyList<Problem>> ValidateModelAsync(DiscountEditModel discount, CancellationToken cancellationToken)
    {
        var problems = new List<Problem>();
        if (discount.StartDate is { } start && discount.EndDate is { } end && end < start)
        {
            problems.Add(ProblemAt(d => d.EndDate, "Must be on or after startDate."));
        }

        if (discount.Code is { Length: > 0 } code && discounts.GetDiscounts().Any(d => d.Id != discount.Key && d.Code == code))
        {
            problems.Add(ProblemAt(d => d.Code, $"Another discount already uses '{code}'."));
        }

        return Task.FromResult<IReadOnlyList<Problem>>(problems);
    }

    protected override string? GetEditUrl(Guid key) => $"/umbraco/section/commerce/discount/{key}";

    private DiscountEditModel ToEditModel(DiscountReadOnly discount) => new()
    {
        Key = discount.Id,
        Version = discount.Version,
        Name = discount.Name,
        Code = discount.Code,
        StartDate = ToDate(discount.StartDate),
        EndDate = ToDate(discount.ExpiryDate),
        LandingPage = discount.LandingPageId,
        Active = discount.IsActive,
        Rules = discount.Rules.Select((r, i) => ToRuleItem(r, DerivedKey(discount.Id, i, r.RuleProviderAlias))).ToList(),
        Rewards = discount.Rewards.Select((r, i) => ToItem(rewardProviders, r.RewardProviderAlias, r.Settings, DerivedKey(discount.Id, i, r.RewardProviderAlias))).ToList(),
    };

    // Rules and rewards are a provider alias plus string settings in Commerce; the framework maps those. Commerce adds
    // only a group rule's children.
    private static DynamicItem ToItem(DiscountProviderCollection providers, string alias, IReadOnlyDictionary<string, string> settings, Guid key)
        => PluginSettings.ToItem(key, alias, settings, providers.Find(alias) is { } provider ? Describe(provider) : null);

    private DynamicItem ToRuleItem(DiscountRuleConfig rule, Guid key)
    {
        var item = ToItem(ruleProviders, rule.RuleProviderAlias, rule.Settings, key);
        if (rule.Children is { Count: > 0 } children)
        {
            item.SetItems(Children, children.Select((c, i) => ToRuleItem(c, DerivedKey(key, i, c.RuleProviderAlias))));
        }

        return item;
    }

    private static DiscountRuleConfig ToRuleConfig(DynamicItem item)
        => new(item.Type, PluginSettings.ToSettings(item, Children), item.Fields.ContainsKey(Children) ? item.Items(Children).Select(ToRuleConfig).ToList() : null);

    private static DateOnly? ToDate(DateTime? utc) => utc is { } value ? DateOnly.FromDateTime(value) : null;

    private static DateTime? ToUtc(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// Commerce's rules and rewards have no ids, so an item's key is derived from its discount, position and provider.
    /// The same rule gets the same key on every load, but a new item's key changes when it is saved, and moving an item
    /// changes its key. Stable ids on rule nodes in Commerce would fix both.
    /// </summary>
    internal static Guid DerivedKey(Guid parent, int index, string alias)
        => new(MD5.HashData(Encoding.UTF8.GetBytes($"{parent:N}/{index}/{alias}")));
}

// ── A product Read ability. Commerce queries what's live; reading through the change set adds the current set's
// changes, so a model building in a set sees what it built. ──────────────────────────────────────────────────

public sealed record ListDiscountsInput([property: Description("Only discounts that are switched on.")] bool ActiveOnly = false);

public sealed record DiscountListing(Guid Key, string Name, bool Active, DateOnly? StartDate, DateOnly? EndDate);

[Ability("commerce.listDiscounts", "List discounts", Kind = AbilityKind.Read,
    Description = "Lists discounts with their dates and whether they're switched on. Inside a change set it shows the set's changes.")]
public sealed class ListDiscountsAbility(DiscountService discounts, DiscountAdapter adapter, ChangeSetService changeSets)
    : AbilityBase<ListDiscountsInput, IReadOnlyList<DiscountListing>>
{
    protected override async Task<AbilityResult> ExecuteAsync(ListDiscountsInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var models = await changeSets.ReadModelsAsync(adapter, discounts.GetDiscounts().Select(d => d.Id), cancellationToken);
        return Success(models
            .Where(d => !input.ActiveOnly || d.Active == true)
            .OrderBy(d => d.Name)
            .Select(d => new DiscountListing(d.Key, d.Name ?? string.Empty, d.Active == true, d.StartDate, d.EndDate))
            .ToList());
    }
}

// ── A product ability that changes a discount, written against the model. ───────────────────────────────

public sealed record ActivateDiscountInput(
    [property: Description("The discount to switch on."), EntityKey("umbraco-commerce-discount")] Guid Discount);

/// <summary>Run directly it saves the discount; inside a change set it is recorded as a change to the discount.</summary>
[Ability("commerce.activateDiscount", "Activate discount", Kind = AbilityKind.Write,
    Description = "Switches a discount on so customers get it.")]
public sealed class ActivateDiscountAbility(DiscountAdapter discounts) : ModelEditAbilityBase<ActivateDiscountInput, DiscountEditModel>(discounts)
{
    protected override Guid KeyOf(ActivateDiscountInput input) => input.Discount;

    protected override void Edit(DiscountEditModel discount, ActivateDiscountInput input) => discount.Active = true;
}
