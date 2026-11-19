using System.ComponentModel;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Abilities.Samples.Commerce.Domain;

namespace Umbraco.Abilities.Samples.Commerce;

public sealed record ListDiscountsInput([property: Description("Only discounts that are switched on.")] bool ActiveOnly = false);

public sealed record DiscountListing(Guid Key, string Name, bool Active, DateOnly? StartDate, DateOnly? EndDate);

/// <summary>
/// A product's own Read ability. Commerce queries what's live; reading through the change set adds the current set's
/// changes, so a model building in a set sees what it built.
/// </summary>
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
