using System.Security.Cryptography;
using System.Text;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Abilities.Samples.Commerce.Domain;

namespace Umbraco.Abilities.Samples.Commerce;

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
    : EntityAdapterBase<DiscountEditModel>(itemTypes, unitOfWork)
{
    /// <summary>Real Commerce would check the Commerce section and the user's access to the discount's store.</summary>
    public SampleAccess? Access { get; set; }

    public override Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(Access?.Authorize(EntityType, request));

    public const string RuleGroup = "commerce.discount.rule";
    public const string RewardGroup = "commerce.discount.reward";

    private const string Children = "children";

    /// <summary>Commerce's table from an editor UI alias to what it picks. Editors not listed stay plain values.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Editors = new Dictionary<string, string[]>
    {
        ["Uc.PropertyEditorUi.CollectionPicker"] = ["umbraco-commerce-collection"],
    };

    /// <summary>
    /// One source per seam. Providers registered later, by any package, show up with no change here. Only users with the
    /// Commerce section are offered them.
    /// </summary>
    protected override IEnumerable<IItemTypeSource> ItemTypeSources() =>
    [
        new PluginItemTypeSource(RuleGroup, () => ruleProviders.Select(p => Describe(p) with { AvailableTo = HasCommerce }), Editors),
        new PluginItemTypeSource(RewardGroup, () => rewardProviders.Select(p => Describe(p) with { AvailableTo = HasCommerce }), Editors),
    ];

    private ValueTask<bool> HasCommerce(Guid user, CancellationToken cancellationToken) => ValueTask.FromResult(Access?.HasSection(user, "commerce") ?? true);

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

    protected override IEnumerable<DiscountEditModel> ListModels() => discounts.GetDiscounts().Select(ToEditModel);

    protected override void Validate(DiscountEditModel discount, ModelProblems<DiscountEditModel> problems)
    {
        if (discount.StartDate is { } start && discount.EndDate is { } end && end < start)
        {
            problems.At(d => d.EndDate, "Must be on or after startDate.");
        }

        if (discount.Code is { Length: > 0 } code && discounts.GetDiscounts().Any(d => d.Id != discount.Key && d.Code == code))
        {
            problems.At(d => d.Code, $"Another discount already uses '{code}'.");
        }
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
