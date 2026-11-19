using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Cms;

// The page's other items: promotions chosen per language, and picked media.

[ItemType("promo", "Promotion", Description = "A promotion shown in one language.")]
public sealed class Promo : EntityItemModel
{
    [Required, Description("Promotion text.")]
    public string? Text { get; set; }
}

[ItemType("mediaEntry", "Media", Description = "A picked media item.")]
public sealed class MediaEntry : EntityItemModel
{
    [Required, EntityKey("media"), Description("The media item.")]
    public Guid? Media { get; set; }

    [Description("Optional focal point.")]
    public FocalPoint? FocalPoint { get; set; }
}

public sealed record FocalPoint(double Left, double Top);
