using Umbraco.Abilities.Entities;

namespace Umbraco.Abilities.Commerce;

/// <summary>The check a task makes when it names the store its discount must be in.</summary>
internal static class DiscountStore
{
    /// <summary>
    /// Refuses a discount from another store. A step built for one store, whose discount was moved or picked by key,
    /// fails with the reason rather than changing a discount it wasn't built for.
    /// </summary>
    public static void Check(DiscountModel discount, Guid? store)
    {
        if (store is { } expected && discount.Store != expected)
        {
            throw new AbilityInputException($"{discount.Name} isn't in the chosen store.", [new Problem("discount", "Not in the chosen store.")]);
        }
    }
}
