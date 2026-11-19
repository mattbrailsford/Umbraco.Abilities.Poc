using System.Security.Cryptography;
using System.Text;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Commerce.Core.Discounts.Rewards;
using Umbraco.Commerce.Core.Discounts.Rules;
using Umbraco.Commerce.Core.Models;

using CommerceDiscount = Umbraco.Commerce.Core.Models.Discount;

namespace Umbraco.Abilities.Commerce;

// How a discount maps to and from Commerce's own model, and how Commerce's rule and reward providers are described.
public sealed partial class CommerceDiscountAdapter
{
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
        // The price editor shows a box per currency, so a person needs no help. A model writes the object itself.
        var hints = s.EditorUiAlias == PriceEditor
            ? UiHints.WithHint(null, AbilityAudiences.Model, $"An object of amounts by currency key, such as {{\"<currency key>\": 50}}. Find the store's currencies with entity.find (entityType {CommerceCurrencyAdapter.Type}).")
            : null;

        return new SettingDescriptor(s.Key, s.Label, s.Description, s.PropertyType, s.ValidationRules, s.EditorUiAlias, s.SortOrder) { Hints = hints };
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
