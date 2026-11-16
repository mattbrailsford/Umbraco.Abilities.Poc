using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Cms;

/// <summary>
/// A culture-variant document type shaped like real CMS content, to check the schema language can carry it:
/// <list type="bullet">
/// <item>the name and some properties vary by culture (<see cref="Varying{T}"/>), others are invariant;</item>
/// <item><c>blocks</c> is an invariant Block List whose blocks hold culture-variant values and have settings (an object);</item>
/// <item><c>grid</c> is a Block Grid: column and row span are a layout object on each block, and areas are items fields inside a block;</item>
/// <item><c>promos</c> is a culture-variant block property: a separate list per culture;</item>
/// <item><c>image</c> is a Media Picker: entries with their own key, a media reference and crop data;</item>
/// <item><c>tags</c> is a string array, and <c>related</c> a multi-node tree picker pointing at documents or media.</item>
/// </list>
/// </summary>
[Entity("document", "Page", Description = "A content page.")]
public sealed class Page : EntityModel
{
    [EntityName, Required, Description("Page name.")]
    public Varying<string> Title { get; set; } = [];

    [Description("URL segment, lower case with hyphens.")]
    public Varying<string> UrlSegment { get; set; } = [];

    [Description("Tags.")]
    public List<string>? Tags { get; set; }

    // Markup plus blocks: paths reach both, and the handler keeps the markup's block tags in step with the blocks.
    [ValueHandler(typeof(RichTextValueHandler)), Description("An introduction: rich text with blocks.")]
    public RichTextValue? Intro { get; set; }

    [EntityKey("document", "media"), Description("Related pages or media.")]
    public List<EntityReference>? Related { get; set; }

    // Allowed blocks come from the data type's configuration at run time: see CmsPageAdapter.AllowedBlocks.
    [Items, Description("The page's content blocks.")]
    public List<PageBlock> Blocks { get; set; } = [];

    // Or named in code, when the list is fixed.
    [Items(typeof(TwoColumn), typeof(RichText)), Description("A layout grid.")]
    public List<GridBlock> Grid { get; set; } = [];

    [Items, Description("Promotions, chosen per language.")]
    public Varying<List<Promo>> Promos { get; set; } = [];

    [Items(Max = 1), Description("The page image.")]
    public List<MediaEntry> Image { get; set; } = [];
}

// ── Block List ──────────────────────────────────────────────────────────────────────────────────────────

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
    [Required, AllowedValues("light", "dark"), Description("Colour theme.")]
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

// ── Block Grid ──────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A grid block: placed in the grid or an area with a column and row span.</summary>
public abstract class GridBlock : EntityItemModel
{
    [Required, Description("Where the block sits in the grid.")]
    public GridPlacement? Layout { get; set; }
}

public sealed class GridPlacement
{
    [Required, Range(1, CmsPageAdapter.GridColumns), Description("Columns the block spans, 1 to 12.")]
    public int? ColumnSpan { get; set; }

    [Description("Rows the block spans.")]
    public int? RowSpan { get; set; } = 1;
}

/// <summary>A layout block whose areas are items fields; area limits are their Min and Max.</summary>
[ItemType("twoColumn", "Two columns", Description = "A row with a left and a right area.")]
public sealed class TwoColumn : GridBlock
{
    [Items(typeof(RichText), Min = 1, Max = 2), Description("Left area. Only rich text.")]
    public List<GridBlock> Left { get; set; } = [];

    [Items(typeof(RichText), Max = 2), Description("Right area. Only rich text.")]
    public List<GridBlock> Right { get; set; } = [];
}

[ItemType("richText", "Rich text", Description = "A block of text.")]
public sealed class RichText : GridBlock
{
    [Required, Description("The text.")]
    public Varying<string> Text { get; set; } = [];
}

// ── Other block properties and pickers ──────────────────────────────────────────────────────────────────

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

// ── Media: request models with no attributes and no base class, configured fluently ───────────────────────

/// <summary>Stands in for a Management API request model in another assembly: a package can't put attributes on it.</summary>
public sealed class MediaRequestModel
{
    public Guid Id { get; set; }

    public long? Version { get; set; }

    public string? Name { get; set; }

    public string? AltText { get; set; }

    public List<MediaCropRequestModel> Crops { get; set; } = [];

    /// <summary>Server-side state the editing contract leaves out.</summary>
    public DateTime? UpdateDate { get; set; }
}

public sealed class MediaCropRequestModel
{
    public Guid Id { get; set; }

    public string? Alias { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }
}

// ── Adapters ─────────────────────────────────────────────────────────────────────────────────────────

public sealed class CmsPageAdapter(InMemoryDatabase database, ItemTypeRegistry itemTypes)
    : InMemoryModelAdapter<Page>(database, itemTypes, "/umbraco/section/content/workspace/document/edit"), IVersionedEntityAdapter
{
    public static readonly IReadOnlyList<string> Cultures = ["en-US", "da-DK"];

    public const int GridColumns = 12;

    /// <summary>
    /// Stands in for the Block List data type's configuration: the blocks an editor allowed on the property. It can
    /// change while the site runs, so it's read each time the field's item types are needed.
    /// </summary>
    public List<Type> AllowedBlocks { get; } = [typeof(Hero), typeof(CardGrid)];

    protected override void AllowItems(ItemAllowList<Page> allow)
        => allow.Field(x => x.Blocks, () => AllowedBlocks);

    protected override Task<IReadOnlyList<string>?> GetDimensionValuesAsync(string dimension, CancellationToken cancellationToken)
        => Task.FromResult(dimension == VariationDimensions.Culture ? Cultures : null);

    /// <summary>Content keeps version history, so change sets don't need their own snapshot of a page.</summary>
    public Task<Entity?> LoadVersionAsync(Guid key, long version, CancellationToken cancellationToken = default)
        => Task.FromResult(LoadVersion(key, version));
}

/// <summary>
/// Media items, so pickers and references can point at media created in the same change set. The model is described
/// fluently, so the request models stay as they are.
/// </summary>
public sealed class CmsMediaAdapter(InMemoryDatabase database, ItemTypeRegistry itemTypes)
    : InMemoryModelAdapter<MediaRequestModel>(database, itemTypes, "/umbraco/section/media/workspace/media/edit")
{
    protected override void Configure(ModelConfigurator<MediaRequestModel> models) => models
        .Entity(media => media
            .IsEntity("media", "Media", "An image or file.")
            .HasKey(x => x.Id)
            .HasVersion(x => x.Version)
            .Ignore(x => x.UpdateDate)
            .Field(x => x.Name).IsName().Required().Description("Media name.")
            .Field(x => x.AltText).Description("Alternative text.")
            .Items(x => x.Crops).Description("Named crops of the image."))
        .Model<MediaCropRequestModel>(crop => crop
            .IsItemType("crop", "Crop", "A named crop.")
            .HasKey(x => x.Id)
            .Field(x => x.Alias).Required().Description("Crop alias, e.g. thumbnail.")
            .Field(x => x.Width).Required().Rule(new RangeAttribute(1, 10000)).Description("Width in pixels.")
            .Field(x => x.Height).Required().Rule(new RangeAttribute(1, 10000)).Description("Height in pixels."));
}
