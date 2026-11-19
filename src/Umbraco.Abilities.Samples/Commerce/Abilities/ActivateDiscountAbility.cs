using System.ComponentModel;

using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Commerce;

public sealed record ActivateDiscountInput(
    [property: Description("The discount to switch on."), EntityKey("umbraco-commerce-discount")] Guid Discount);

/// <summary>Run directly it saves the discount; inside a change set it is recorded as a change to the discount.</summary>
[Ability("commerce.activateDiscount", "Activate discount", Kind = AbilityKind.Write,
    Description = "Switches a discount on so customers get it.")]

public sealed class ActivateDiscountAbility(DiscountAdapter discounts) : ModelEditAbilityBase<ActivateDiscountInput, DiscountEditModel>(discounts)
{
    protected override void Edit(DiscountEditModel discount, ActivateDiscountInput input) => discount.Active = true;
}
