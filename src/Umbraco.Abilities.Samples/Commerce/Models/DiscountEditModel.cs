using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Commerce;

/// <summary>
/// The editing contract for a discount: a plain C# class, separate from Commerce's domain model
/// (<see cref="Domain.Discount"/>). Everything the AI and change sets need is derived from it; <see cref="DiscountAdapter"/>
/// maps it to and from the domain.
/// </summary>
[Entity("umbraco-commerce-discount", "Discount", Description = "A discount applied when all its rules match.")]
public sealed class DiscountEditModel : EntityModel
{
    [EntityName, Required, Description("Name shown to editors.")]
    public string? Name { get; set; }

    [Description("Optional coupon code. Unique.")]
    public string? Code { get; set; }

    [Description("First day the discount applies.")]
    public DateOnly? StartDate { get; set; }

    [Description("Last day the discount applies.")]
    public DateOnly? EndDate { get; set; }

    [EntityKey("document"), Description("Optional page that promotes the discount.")]
    public Guid? LandingPage { get; set; }

    [Description("Whether the discount is live."), DefaultValue(false)]
    [Hint(AbilityAudiences.Model, "Use commerce.activateDiscount to switch it on.")]
    [Hint(AbilityAudiences.Person, "Leave it off to check the discount before customers get it.")]
    public bool? Active { get; set; }

    [Items(DiscountAdapter.RuleGroup, Min = 1), Description("All rules must match for the discount to apply.")]
    public List<DynamicItem> Rules { get; set; } = [];

    [Items(DiscountAdapter.RewardGroup, Min = 1, Max = 3), Description("What the customer gets.")]
    public List<DynamicItem> Rewards { get; set; } = [];
}

// Rules and rewards are a plugin seam: Commerce and other packages register providers (Providers.cs), so their item
// types come from the provider collections at run time (a PluginItemTypeSource per seam, below), not from classes here.
