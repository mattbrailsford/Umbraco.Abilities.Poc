using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Commerce;

public sealed record AddDiscountCodeInput(
    [property: Description("The discount to add the code to. It must be a 'code' discount."), EntityKey(CommerceDiscountAdapter.Type, Within = "Store")] Guid Discount,
    [property: Description("The code to add, such as WELCOME-JO. Leave it empty and one is made.")] string? Code = null,
    [property: Description("The store the discount must be in. Optional: it narrows the discounts to choose from."), EntityKey(CommerceStoreAdapter.Type), JsonPropertyOrder(-1)] Guid? Store = null);

/// <summary>The code added, to pass on to a customer, for example in an email.</summary>
public sealed record DiscountCodeAdded(
    [property: Description("The discount the code belongs to.")] Guid Discount,
    [property: Description("The discount's name.")] string DiscountName,
    [property: Description("The code the customer enters at checkout.")] string Code);

/// <summary>
/// A task Commerce offers: give out one more code for an existing discount, such as one per customer who signs up. The
/// discount's rules and rewards are set up once, by a person, and stay as they are.
/// </summary>
[Ability("commerce.discount.addCode", "Add a discount code", Kind = AbilityKind.Write,
    Description = "Adds a code to an existing code discount, and returns it. Leave the code empty and a unique one is made.")]
public sealed class AddDiscountCodeAbility(CommerceDiscountAdapter discounts)
    : ModelEditAbilityBase<AddDiscountCodeInput, DiscountModel, DiscountCodeAdded>(discounts)
{
    // No 0/O or 1/I, so a customer can read it back.
    private const string Letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    // Made once, before the call is recorded, so the code the step returns is the code that is saved.
    protected override AddDiscountCodeInput Fill(AddDiscountCodeInput input)
        => string.IsNullOrWhiteSpace(input.Code) ? input with { Code = RandomNumberGenerator.GetString(Letters, 10) } : input with { Code = input.Code.Trim() };

    protected override void Edit(DiscountModel discount, AddDiscountCodeInput input)
    {
        DiscountStore.Check(discount, input.Store);
        if (discount.Type != DiscountKind.Code)
        {
            throw new AbilityInputException(
                $"{discount.Name} applies automatically, so it takes no codes. Choose a 'code' discount.",
                [new Problem("discount", "Not a code discount.")]);
        }

        if (discount.Codes.Contains(input.Code!, StringComparer.OrdinalIgnoreCase))
        {
            throw new AbilityInputException($"{discount.Name} already has the code {input.Code}.", [new Problem("code", "Already in use.")]);
        }

        discount.Codes.Add(input.Code!);
    }

    protected override DiscountCodeAdded Result(DiscountModel discount, AddDiscountCodeInput input, EntitySummary entity)
        => new(entity.Key, discount.Name ?? string.Empty, input.Code!);
}
