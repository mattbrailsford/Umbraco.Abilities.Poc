using System.ComponentModel;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Commerce;

/// <summary>A currency, as a model and a person see it. Read only: only so an amount can be keyed by one.</summary>
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
