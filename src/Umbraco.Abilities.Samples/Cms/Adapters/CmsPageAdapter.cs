using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Cms;

/// <summary>Pages, stored in memory: the site's cultures, the blocks the data type allows, and version history.</summary>
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
