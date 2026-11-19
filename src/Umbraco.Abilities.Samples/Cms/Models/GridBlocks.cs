using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Cms;

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
