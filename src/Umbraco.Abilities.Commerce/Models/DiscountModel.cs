using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Commerce.Core.Models;

namespace Umbraco.Abilities.Commerce;

/// <summary>When a discount applies: whenever its rules match, or only with one of its codes.</summary>
public enum DiscountKind
{
    Automatic,
    Code,
}

/// <summary>
/// The editing contract for a Commerce discount: what a model and a person see. Commerce's own domain model stays as it
/// is; <see cref="CommerceDiscountAdapter"/> maps it to and from this.
/// </summary>
[Entity(CommerceDiscountAdapter.Type, "Discount", Description = "An Umbraco Commerce discount: given when its rules match.")]
public sealed class DiscountModel : EntityModel
{
    [EntityName, Required, Description("Name shown to editors.")]
    public string? Name { get; set; }

    [Required, EntityKey(CommerceStoreAdapter.Type), Description("The store the discount belongs to.")]
    [Hint(AbilityAudiences.Model, "Find stores with entity.find.")]
    public Guid? Store { get; set; }

    [Description("A short unique name for code. Made from the name when left empty.")]
    public string? Alias { get; set; }

    [Description("'automatic' applies whenever the rules match; 'code' needs one of the codes at checkout."), DefaultValue(DiscountKind.Automatic)]
    public DiscountKind? Type { get; set; }

    [Description("The codes a customer can enter, for a 'code' discount. Each must be unique among the store's active discounts.")]
    public List<string> Codes { get; set; } = [];

    [Description("First day the discount applies.")]
    public DateOnly? StartDate { get; set; }

    [Description("Last day the discount applies.")]
    public DateOnly? EndDate { get; set; }

    [Description("Whether the discount is switched on."), DefaultValue(false)]
    public bool? Active { get; set; }

    [Description("Once this discount applies, no later discount does."), DefaultValue(false)]
    public bool? BlockFurtherDiscounts { get; set; }

    [Description("Doesn't apply if an earlier discount already did."), DefaultValue(false)]
    public bool? BlockIfPreviousDiscounts { get; set; }

    [Description("How the rules combine: 'all' must match, 'any' one of them, or 'funnel' (each rule narrows the order lines the next one sees)."), DefaultValue(GroupDiscountRuleMatchType.All)]
    public GroupDiscountRuleMatchType? RulesMatch { get; set; }

    [Items(CommerceDiscountAdapter.RuleGroup), Description("When the discount applies. None: it always applies.")]
    public List<DynamicItem> Rules { get; set; } = [];

    [Items(CommerceDiscountAdapter.RewardGroup, Min = 1), Description("What the customer gets.")]
    public List<DynamicItem> Rewards { get; set; } = [];
}
