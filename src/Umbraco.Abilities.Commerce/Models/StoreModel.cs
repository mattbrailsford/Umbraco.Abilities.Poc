using System.ComponentModel;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Commerce;

/// <summary>A store, as a model and a person see it. Read only: only so a discount can point at one.</summary>
[Entity(CommerceStoreAdapter.Type, "Store", Description = "An Umbraco Commerce store. Discounts belong to one.")]
public sealed class StoreModel : EntityModel
{
    [EntityName, Description("Store name.")]
    public string? Name { get; set; }

    [Description("The store's alias.")]
    public string? Alias { get; set; }
}
