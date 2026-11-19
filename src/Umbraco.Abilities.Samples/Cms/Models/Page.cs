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
