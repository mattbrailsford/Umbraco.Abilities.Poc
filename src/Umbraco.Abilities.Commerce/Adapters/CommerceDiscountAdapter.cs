using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Discounts.Rewards;
using Umbraco.Commerce.Core.Discounts.Rules;

namespace Umbraco.Abilities.Commerce;

/// <summary>
/// Maps Commerce's discounts to and from <see cref="DiscountModel"/>, through Commerce's own C# API. Rules and rewards
/// are Commerce's provider collections, so a provider any package adds is offered with no change here. Validating runs
/// the real save and rolls it back, so Commerce's own checks (a code already in use, a duplicate alias) show up before
/// the set is applied, not only when it is.
/// </summary>
public sealed partial class CommerceDiscountAdapter(
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
}
