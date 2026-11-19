using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Cms;

/// <summary>A Block List block: what <see cref="Page.Blocks"/> holds.</summary>
public abstract class PageBlock : EntityItemModel;

/// <summary>Its heading varies by culture inside an invariant property, and it has a settings element.</summary>
[ItemType("hero", "Hero", Description = "A large heading at the top of a page.")]
public sealed class Hero : PageBlock
{
    [Required, Description("Main heading.")]
    public Varying<string> Heading { get; set; } = [];

    [Description("Line under the heading.")]
    public Varying<string> Subheading { get; set; } = [];

    [Required, Description("Hero settings.")]
    public HeroSettings? Settings { get; set; }
}

public sealed class HeroSettings
{
    // A third party's own editor for the value: a UI shows swatches, a model sees only the allowed values. The hint
    // under acme.theme is Acme's, and only Acme's editor reads it.
    [Required, AllowedValues("light", "dark"), Description("Colour theme.")]
    [Editor("Acme.ThemePicker", Config = """{ "swatches": true }""")]
    [Meta("acme.theme", """{ "preview": "hero" }""")]
    public string? Theme { get; set; }

    [Description("Span the whole page width.")]
    public bool? FullWidth { get; set; }
}

/// <summary>A block with a block property of its own: an items field inside an item.</summary>
[ItemType("cardGrid", "Card grid", Description = "A grid of cards.")]
public sealed class CardGrid : PageBlock
{
    [Description("Heading above the grid.")]
    public Varying<string> Heading { get; set; } = [];

    [Items(Min = 1), Description("The cards in the grid.")]
    public List<Card> Cards { get; set; } = [];
}

[ItemType("card", "Card", Description = "A card with a title and text.")]
public sealed class Card : EntityItemModel
{
    [Required, Description("Card title.")]
    public string? Title { get; set; }

    [Description("Card text.")]
    public string? Text { get; set; }
}
