using System.ComponentModel;
using System.Text.Json.Serialization;

using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Commerce;

public sealed record SetDiscountActiveInput(
    [property: Description("The discount to switch on or off."), EntityKey(CommerceDiscountAdapter.Type, Within = "Store")] Guid Discount,
    [property: Description("True to switch it on, so customers get it. False to switch it off.")] bool Active,
    [property: Description("The store the discount must be in. Optional: it narrows the discounts to choose from."), EntityKey(CommerceStoreAdapter.Type), JsonPropertyOrder(-1)] Guid? Store = null);

/// <summary>
/// A task Commerce offers: switch an existing discount on or off, such as at the start and end of a sale. Its rules,
/// rewards and dates stay as they are.
/// </summary>
[Ability("commerce.discount.setActive", "Switch a discount on or off", Kind = AbilityKind.Write,
    Description = "Switches an existing discount on, so customers get it, or off.")]
public sealed class SetDiscountActiveAbility(CommerceDiscountAdapter discounts) : ModelEditAbilityBase<SetDiscountActiveInput, DiscountModel>(discounts)
{
    protected override void Edit(DiscountModel discount, SetDiscountActiveInput input)
    {
        DiscountStore.Check(discount, input.Store);
        discount.Active = input.Active;
    }
}
