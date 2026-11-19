using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Commerce;

/// <summary>A product collection, so a discount rule can point at one.</summary>
[Entity("umbraco-commerce-collection", "Collection", Description = "A product collection.")]
public sealed class Collection : EntityModel
{
    [EntityName, Required, Description("Collection name.")]
    public string? Name { get; set; }
}
