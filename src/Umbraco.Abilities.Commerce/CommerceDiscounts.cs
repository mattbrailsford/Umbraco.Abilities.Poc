using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Discounts.Rewards;
using Umbraco.Commerce.Core.Discounts.Rules;
using Umbraco.Commerce.Core.Models;

using CommerceDiscount = Umbraco.Commerce.Core.Models.Discount;

namespace Umbraco.Abilities.Commerce;

// ── The editing contracts: what a model and a person see. Commerce's own domain model stays as it is; the adapters
// below map it to and from these. ─────────────────────────────────────────────────────────────────────────────

[Entity(CommerceStoreAdapter.Type, "Store", Description = "An Umbraco Commerce store. Discounts belong to one.")]
public sealed class StoreModel : EntityModel
{
    [EntityName, Description("Store name.")]
    public string? Name { get; set; }

    [Description("The store's alias.")]
    public string? Alias { get; set; }
}

[Entity(CommerceCurrencyAdapter.Type, "Currency", Description = "A currency a Commerce store sells in. Amounts in discount settings are keyed by currency key.")]
public sealed class CurrencyModel : EntityModel
{
    [EntityName, Description("Currency name.")]
    public string? Name { get; set; }

    [Description("ISO code, such as GBP or EUR.")]
    public string? Code { get; set; }

    [EntityKey(CommerceStoreAdapter.Type), Description("The store it belongs to.")]
    public Guid? Store { get; set; }
}

/// <summary>When a discount applies: whenever its rules match, or only with one of its codes.</summary>
public enum DiscountKind
{
    Automatic,
    Code,
}

[Entity(CommerceDiscountAdapter.Type, "Discount", Description = "An Umbraco Commerce discount: given when its rules match.")]
public sealed class DiscountModel : EntityModel
{
    [EntityName, Required, Description("Name shown to editors.")]
    public string? Name { get; set; }

    [Required, EntityKey(CommerceStoreAdapter.Type), Description("The store the discount belongs to. Find stores with entity.find.")]
    public Guid? Store { get; set; }

    [Description("A short unique name for code. Made from the name when left empty.")]
    public string? Alias { get; set; }

    [Description("'automatic' applies whenever the rules match; 'code' needs one of the codes at checkout."), DefaultValue(DiscountKind.Automatic)]
    public DiscountKind? Type { get; set; }

    [Description("The codes a customer can enter, for a 'code' discount. Each must be unique among the store's active discounts.")]
    public List<string> Codes { get; set; } = [];

    [Description("First day the discount applies.")]
    public DateOnly? StartDate { get; set; }

    [Description("Last day the discount applies.")]
    public DateOnly? EndDate { get; set; }

    [Description("Whether the discount is switched on."), DefaultValue(false)]
    public bool? Active { get; set; }

    [Description("Once this discount applies, no later discount does."), DefaultValue(false)]
    public bool? BlockFurtherDiscounts { get; set; }

    [Description("Doesn't apply if an earlier discount already did."), DefaultValue(false)]
    public bool? BlockIfPreviousDiscounts { get; set; }

    [Description("How the rules combine: 'all' must match, 'any' one of them, or 'funnel' (each rule narrows the order lines the next one sees)."), DefaultValue(GroupDiscountRuleMatchType.All)]
    public GroupDiscountRuleMatchType? RulesMatch { get; set; }

    [Items(CommerceDiscountAdapter.RuleGroup), Description("When the discount applies. None: it always applies.")]
    public List<DynamicItem> Rules { get; set; } = [];

    [Items(CommerceDiscountAdapter.RewardGroup, Min = 1), Description("What the customer gets.")]
    public List<DynamicItem> Rewards { get; set; } = [];
}

// ── The adapters. ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Commerce's transaction, for a step of applying a change set. Commerce's unit of work only runs a callback, and may
/// retry it, which is the shape <see cref="IUnitOfWorkProvider.RunAsync"/> has.
/// </summary>
public sealed class CommerceUnitOfWorkProvider(IUmbracoCommerceApi commerce) : IUnitOfWorkProvider
{
    public string Name => "Commerce";

    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            await work(ct);
            uow.Complete();
        }, cancellationToken);
}

/// <summary>Who may use a store: users Commerce allows, by user or user group, who also have the Commerce section.</summary>
public sealed class CommerceAccess(IUserService users)
{
    public const string Section = "commerce";

    public async Task<string?> CheckAsync(Guid userKey, StoreReadOnly? store)
    {
        if (await users.GetAsync(userKey) is not { } user)
        {
            return "Unknown user.";
        }

        if (!user.AllowedSections.Contains(Section))
        {
            return "You don't have the Commerce section.";
        }

        return store is null || store.IsAllowed(user.Key.ToString(), user.Groups.Select(g => g.Key.ToString()))
            ? null
            : $"You don't have access to the store '{store.Name}'.";
    }
}

/// <summary>Stores, so a discount can point at one. Read only: stores are set up in the backoffice.</summary>
public sealed class CommerceStoreAdapter(IUmbracoCommerceApi commerce, CommerceAccess access, ItemTypeRegistry itemTypes)
    : EntityAdapterBase<StoreModel>(itemTypes, new CommerceUnitOfWorkProvider(commerce))
{
    public const string Type = "umbraco-commerce-store";

    public override bool CanCreate => false;

    public override async Task<StoreModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
        => await commerce.GetStoreAsync(key) is { } store ? new StoreModel { Key = store.Id, Name = store.Name, Alias = store.Alias } : null;

    public override Task SaveModelAsync(StoreModel model, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stores are set up in the backoffice.");

    public override async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => await access.CheckAsync(request.UserKey, await commerce.GetStoreAsync(request.Key));

    protected override async Task<IReadOnlyList<StoreModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
        => (await commerce.GetStoresAsync())
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Alias.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => new StoreModel { Key = s.Id, Name = s.Name, Alias = s.Alias })
            .ToList();

    protected override string? GetEditUrl(Guid key) => $"/umbraco/section/commerce/workspace/uc:store/edit/{key}";
}

/// <summary>
/// A store's currencies, so a model can fill in an amount, which Commerce keys by currency. Read only: currencies are
/// set up in the backoffice.
/// </summary>
public sealed class CommerceCurrencyAdapter(IUmbracoCommerceApi commerce, CommerceAccess access, ItemTypeRegistry itemTypes)
    : EntityAdapterBase<CurrencyModel>(itemTypes, new CommerceUnitOfWorkProvider(commerce))
{
    public const string Type = "umbraco-commerce-currency";

    public override bool CanCreate => false;

    public override async Task<CurrencyModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
        => await commerce.GetCurrencyAsync(key) is { } currency ? ToModel(currency) : null;

    public override Task SaveModelAsync(CurrencyModel model, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Currencies are set up in the backoffice.");

    public override async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => await access.CheckAsync(request.UserKey, await commerce.GetCurrencyAsync(request.Key) is { } c ? await commerce.GetStoreAsync(c.StoreId) : null);

    protected override async Task<IReadOnlyList<CurrencyModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
    {
        var found = new List<CurrencyModel>();
        foreach (var store in await commerce.GetStoresAsync())
        {
            found.AddRange((await commerce.GetCurrenciesAsync(store.Id))
                .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Code.Contains(query, StringComparison.OrdinalIgnoreCase) || store.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(ToModel));
        }

        return found;
    }

    protected override string? GetEditUrl(Guid key) => null;

    private static CurrencyModel ToModel(CurrencyReadOnly currency)
        => new() { Key = currency.Id, Name = currency.Name, Code = currency.Code, Store = currency.StoreId };
}

/// <summary>
/// Maps Commerce's discounts to and from <see cref="DiscountModel"/>, through Commerce's own C# API. Rules and rewards
/// are Commerce's provider collections, so a provider any package adds is offered with no change here. Validating runs
/// the real save and rolls it back, so Commerce's own checks (a code already in use, a duplicate alias) show up before
/// the set is applied, not only when it is.
/// </summary>
public sealed class CommerceDiscountAdapter(
    IUmbracoCommerceApi commerce,
    DiscountRuleProviderCollection ruleProviders,
    DiscountRewardProviderCollection rewardProviders,
    CommerceAccess access,
    ItemTypeRegistry itemTypes)
    : EntityAdapterBase<DiscountModel>(itemTypes, new CommerceUnitOfWorkProvider(commerce))
{
    public const string Type = "umbraco-commerce-discount";
    public const string RuleGroup = "umbraco-commerce.discount.rule";
    public const string RewardGroup = "umbraco-commerce.discount.reward";

    private const string GroupRule = "groupDiscountRule";
    private const string MatchType = "matchType";
    private const string Children = "children";
    private const string PriceEditor = "Uc.PropertyEditorUi.Price";

    public override async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
    {
        var storeId = request.Entity?.Fields.TryGetPropertyValue("store", out var store) == true && Guid.TryParse(store?.ToString(), out var id)
            ? id
            : (await commerce.GetDiscountAsync(request.Key))?.StoreId;
        return await access.CheckAsync(request.UserKey, storeId is { } s ? await commerce.GetStoreAsync(s) : null);
    }

    // Settings are stored with the default JSON options, which match Commerce's: enum member names (its EnumHelper),
    // and amounts as numbers. Commerce's own converter is internal to its Management API.

    /// <summary>One source per seam: Commerce's rule and reward providers, read each time, so later ones show up.</summary>
    protected override IEnumerable<IItemTypeSource> ItemTypeSources() =>
    [
        new PluginItemTypeSource(RuleGroup, () => ruleProviders.Where(p => p.Alias != GroupRule).Select(DescribeRule).Append(DescribeGroup())),
        new PluginItemTypeSource(RewardGroup, () => rewardProviders.Select(DescribeReward)),
    ];

    public override async Task<DiscountModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
        => await commerce.GetDiscountAsync(key) is { IsDeleted: false } discount ? ToModel(discount) : null;

    public override async Task SaveModelAsync(DiscountModel model, CancellationToken cancellationToken = default)
    {
        // The step's Commerce unit of work is ambient, so this joins it.
        await commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            await commerce.SaveDiscountAsync(await ToDiscountAsync(uow, model), ct);
            uow.Complete();
        }, cancellationToken);
    }

    protected override async Task ValidateModelAsync(DiscountModel model, ModelProblems<DiscountModel> problems, CancellationToken cancellationToken)
    {
        if (model.StartDate is { } start && model.EndDate is { } end && end < start)
        {
            problems.At(d => d.EndDate, "Must be on or after startDate.");
        }

        if (model.Type == DiscountKind.Code && model.Codes.Count == 0)
        {
            problems.At(d => d.Codes, "A 'code' discount needs at least one code.");
        }

        if (model.Store is null || await commerce.GetStoreAsync(model.Store.Value) is null)
        {
            return;
        }

        // Commerce checks as it writes. Run the real save and don't complete it, so it rolls back.
        try
        {
            await commerce.Uow.ExecuteAsync(autoComplete: false, async (uow, ct) =>
                await commerce.SaveDiscountAsync(await ToDiscountAsync(uow, model), ct), cancellationToken);
        }
        catch (Umbraco.Commerce.Common.Validation.ValidationException ex)
        {
            foreach (var error in ex.Errors)
            {
                problems.At(d => d.Name, $"Commerce: {error.Message}");
            }
        }
    }

    protected override async Task<IReadOnlyList<DiscountModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
    {
        var found = new List<DiscountModel>();
        foreach (var store in await commerce.GetStoresAsync())
        {
            found.AddRange((await commerce.GetDiscountsAsync(store.Id))
                .Where(d => d.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(ToModel));
        }

        return found;
    }

    protected override string? GetEditUrl(Guid key) => $"/umbraco/section/commerce/workspace/uc:discount/edit/{key}";

    private DiscountModel ToModel(DiscountReadOnly discount)
    {
        var root = discount.Rules;
        var isGroup = root?.RuleProviderAlias == GroupRule;
        var rules = root is null ? [] : isGroup ? root.Children ?? [] : (IEnumerable<DiscountRuleConfig>)[root];
        return new DiscountModel
        {
            Key = discount.Id,
            Name = discount.Name,
            Store = discount.StoreId,
            Alias = discount.Alias,
            Type = discount.Type == DiscountType.Code ? DiscountKind.Code : DiscountKind.Automatic,
            Codes = discount.Codes.Select(c => c.Code).ToList(),
            StartDate = ToDate(discount.StartDate),
            EndDate = ToDate(discount.ExpiryDate),
            Active = discount.IsActive,
            BlockFurtherDiscounts = discount.BlockFurtherDiscounts,
            BlockIfPreviousDiscounts = discount.BlockIfPreviousDiscounts,
            RulesMatch = isGroup && root!.Settings.TryGetValue(MatchType, out var match) && Enum.TryParse<GroupDiscountRuleMatchType>(match, true, out var parsed)
                ? parsed
                : GroupDiscountRuleMatchType.All,
            Rules = rules.Select((r, i) => ToRuleItem(r, DerivedKey(discount.Id, i, r.RuleProviderAlias))).ToList(),
            Rewards = discount.Rewards.Select((r, i) => PluginSettings.ToItem(DerivedKey(discount.Id, 100 + i, r.RewardProviderAlias), r.RewardProviderAlias, r.Settings, RewardPlugin(r.RewardProviderAlias))).ToList(),
        };
    }

    private async Task<CommerceDiscount> ToDiscountAsync(Umbraco.Commerce.Common.IUnitOfWork uow, DiscountModel model)
    {
        var alias = string.IsNullOrWhiteSpace(model.Alias) ? AliasFor(model.Name!) : model.Alias;
        var discount = await commerce.GetDiscountAsync(model.Key) is { } existing
            ? await existing.AsWritableAsync(uow)
            : await CommerceDiscount.CreateAsync(uow, model.Key, model.Store!.Value, alias, model.Name!);

        var rules = new DiscountRuleConfig(GroupRule,
            [KeyValuePair.Create(MatchType, (model.RulesMatch ?? GroupDiscountRuleMatchType.All).ToString())],
            model.Rules.Select(ToRuleConfig).ToList());

        await discount.SetNameAsync(model.Name!, alias);
        await discount.SetTypeAsync(model.Type == DiscountKind.Code ? DiscountType.Code : DiscountType.Automatic);
        await discount.SetDateRangeAsync(ToUtc(model.StartDate), ToUtc(model.EndDate));
        await discount.SetCodesAsync(model.Codes.Distinct().Select(c => new DiscountCode(c, null, true)), SetBehavior.Replace);
        await discount.SetRulesAsync(rules, model.BlockFurtherDiscounts ?? false, model.BlockIfPreviousDiscounts ?? false);
        await discount.SetRewardsAsync(model.Rewards.Select(r => new DiscountRewardConfig(r.Type, PluginSettings.ToSettings(r, RewardPlugin(r.Type)))).ToList());
        await discount.SetActiveAsync(model.Active ?? false);
        return discount;
    }

    private DynamicItem ToRuleItem(DiscountRuleConfig rule, Guid key)
    {
        var item = PluginSettings.ToItem(key, rule.RuleProviderAlias, rule.Settings, RulePlugin(rule.RuleProviderAlias));
        if (rule.Children is { Count: > 0 } children)
        {
            item.SetItems(Children, children.Select((c, i) => ToRuleItem(c, DerivedKey(key, i, c.RuleProviderAlias))));
        }

        return item;
    }

    private PluginDescriptor? RulePlugin(string alias)
        => alias == GroupRule ? DescribeGroup() : ruleProviders.FirstOrDefault(p => p.Alias == alias) is { } p ? DescribeRule(p) : null;

    private PluginDescriptor? RewardPlugin(string alias)
        => rewardProviders.FirstOrDefault(p => p.Alias == alias) is { } p ? DescribeReward(p) : null;

    private DiscountRuleConfig ToRuleConfig(DynamicItem item)
        => new(item.Type, PluginSettings.ToSettings(item, RulePlugin(item.Type), Children),
            item.Fields.ContainsKey(Children) ? item.Items(Children).Select(ToRuleConfig).ToList() : []);

    private static PluginDescriptor DescribeRule(IDiscountRuleProvider provider)
        => new(provider.Alias, provider.Label, provider.Description, provider.SettingDefinitions.Select(Describe).ToList());

    private static PluginDescriptor DescribeReward(IDiscountRewardProvider provider)
        => new(provider.Alias, provider.Label, provider.Description, provider.SettingDefinitions.Select(Describe).ToList());

    // Commerce's price editor stores one amount per currency. Say so, as the setting's type alone doesn't.
    private static SettingDescriptor Describe(UmbracoCommerceSettingDefinition s)
    {
        var description = s.Description;
        if (s.EditorUiAlias == PriceEditor)
        {
            description = $"{description} An object of amounts by currency key, such as {{\"<currency key>\": 50}}. Find the store's currencies with entity.find (entityType {CommerceCurrencyAdapter.Type}).".Trim();
        }

        return new SettingDescriptor(s.Key, s.Label, description, s.PropertyType, s.ValidationRules, s.EditorUiAlias, s.SortOrder);
    }

    // A group inside the rules: its own match type, and child rules.
    private static PluginDescriptor DescribeGroup() => new(
        GroupRule,
        "Group",
        "Rules that match together: all, any, or as a funnel.",
        [new SettingDescriptor(MatchType, "Match", "'All', 'Any' or 'Funnel'.", typeof(GroupDiscountRuleMatchType), [], null, 0)])
    {
        ExtraFields = [FieldDefinition.ItemsOf(Children, "The rules in this group.", RuleGroup) with { Min = 1 }],
    };

    private static string AliasFor(string name)
    {
        var words = name.Split((char[])[' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        return string.Concat(words.Select((w, i) => i == 0 ? w.ToLowerInvariant() : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static DateOnly? ToDate(DateTime? utc) => utc is { } value ? DateOnly.FromDateTime(value) : null;

    private static DateTime? ToUtc(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// Commerce's rules and rewards have no ids, so an item's key is derived from its discount, position and provider.
    /// Stable ids on rule nodes in Commerce would make moving an item keep its key.
    /// </summary>
    private static Guid DerivedKey(Guid parent, int index, string alias)
        => new(MD5.HashData(Encoding.UTF8.GetBytes($"{parent:N}/{index}/{alias}")));
}
