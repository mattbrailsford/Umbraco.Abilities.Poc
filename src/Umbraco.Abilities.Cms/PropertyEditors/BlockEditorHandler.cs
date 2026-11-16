using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Abilities.Cms.PropertyEditors;

/// <summary>
/// Block List and Block Grid: a block editor property is an items field, one item per block. Each data type has its own
/// item type group (<c>cms.blocks:{dataTypeKey}</c>), because the same element type can be used by several block
/// editors with different settings types and areas. The group's types are read from the data type each call, so they
/// follow its configuration. A grid has a second group for its areas (<c>cms.blocks:{dataTypeKey}/areas</c>).
/// <para>
/// A block's own fields are its element type's. Its settings are an object field, <c>settings</c>; a grid block's
/// placement is an object field, <c>layout</c>; each of its areas is an items field. Values are read and written with
/// Umbraco's own block value classes.
/// </para>
/// </summary>
public sealed class BlockEditorHandler(IContentTypeService contentTypes, IDataTypeService dataTypes, IJsonSerializer json) : IPropertyEditorEntityHandler, IPropertyEditorItemTypes
{
    public const string SettingsField = "settings";
    public const string LayoutField = "layout";
    public const string GroupPrefix = "cms.blocks:";

    private const string AreasSuffix = "/areas";

    // A block's settings key, kept on the item so a save reuses it. Not a field: '$' marks product data.
    private const string SettingsKeyProperty = "$settingsKey";

    // The settings element type of a block its data type no longer allows, which the configuration can't tell.
    private const string SettingsTypeProperty = "$settingsType";

    // Umbraco points into a block value either by index, ".contentData[0].values[2].value", or, for a value
    // that is missing, by filter: ".contentData[1].values[?(@.alias == 'heading' && @.culture == 'en-US' && @.segment == null)].value".
    private static readonly Regex BlockValuePath = new(
        @"^\.?(?<data>contentData|settingsData)\[(?<item>\d+)\]\.values\[(?:(?<value>\d+)|\?\(@\.alias == '(?<alias>[^']+)' && @\.culture == (?:'(?<culture>[^']*)'|null) && @\.segment == (?:'(?<segment>[^']*)'|null)\))\]\.value",
        RegexOptions.Compiled);

    /// <summary>The item type group of a block editor data type's blocks.</summary>
    public static string GroupOf(Guid dataTypeKey) => $"{GroupPrefix}{dataTypeKey}";

    /// <summary>The item type group of the blocks a Block Grid data type allows in areas.</summary>
    public static string AreaGroupOf(Guid dataTypeKey) => $"{GroupOf(dataTypeKey)}{AreasSuffix}";

    public bool Handles(string editorAlias)
        => editorAlias is Constants.PropertyEditors.Aliases.BlockList or Constants.PropertyEditors.Aliases.BlockGrid;

    // ── The field and its item types ────────────────────────────────────────────────────────

    public Task<FieldDefinition> DescribeAsync(PropertyEditorContext context, CancellationToken cancellationToken = default)
    {
        var dataType = context.DataType ?? throw new InvalidOperationException($"No data type for property '{context.Property.Alias}'.");
        var (min, max) = dataType.ConfigurationObject switch
        {
            BlockGridConfiguration grid => (grid.ValidationLimit.Min, grid.ValidationLimit.Max),
            BlockListConfiguration list => (list.ValidationLimit.Min, list.ValidationLimit.Max),
            _ => (null, null),
        };

        var field = context.Items(GroupOf(dataType.Key));
        return Task.FromResult(field with { Min = min ?? field.Min, Max = max });
    }

    public bool Serves(string group) => group.StartsWith(GroupPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The blocks a group's data type allows now. A Block List's group holds every configured block. A Block Grid has
    /// two: the root (blocks allowed at the root, or not in areas) and its areas (blocks allowed in areas).
    /// </summary>
    public async Task<IEnumerable<ItemTypeDefinition>> GetItemTypesAsync(string group, IElementSchemas elements, CancellationToken cancellationToken = default)
    {
        var name = group[GroupPrefix.Length..];
        var inAreas = name.EndsWith(AreasSuffix, StringComparison.Ordinal);
        var dataTypeKey = Guid.Parse(inAreas ? name[..^AreasSuffix.Length] : name);
        var dataType = await dataTypes.GetAsync(dataTypeKey);
        var types = new List<ItemTypeDefinition>();

        if (dataType?.ConfigurationObject is BlockGridConfiguration grid)
        {
            var columns = grid.GridColumns ?? 12;
            var layout = FieldDefinition.ObjectOf(LayoutField, "Where the block sits in the grid.", ObjectSchema.Of(
                new FieldDefinition("columnSpan", $"Columns the block spans, 1 to {columns}.", FieldType.Integer, Required: true),
                new FieldDefinition("rowSpan", "Rows the block spans.", FieldType.Integer))) with
            { Required = true };

            foreach (var block in grid.Blocks.Where(b => inAreas ? b.AllowInAreas : b.AllowAtRoot || !b.AllowInAreas))
            {
                var areas = block.Areas.Select(a => FieldDefinition.ItemsOf(a.Alias ?? a.Key.ToString(), $"Area '{a.Alias}'.", AreaGroupOf(dataTypeKey)) with
                {
                    Min = a.MinAllowed,
                    Max = a.MaxAllowed,
                });
                types.Add(await BlockTypeAsync(group, block.ContentElementTypeKey, block.SettingsElementTypeKey, [.. areas, layout], elements, cancellationToken));
            }
        }
        else if (dataType?.ConfigurationObject is BlockListConfiguration list && !inAreas)
        {
            foreach (var block in list.Blocks)
            {
                types.Add(await BlockTypeAsync(group, block.ContentElementTypeKey, block.SettingsElementTypeKey, [], elements, cancellationToken));
            }
        }

        return types;
    }

    private async Task<ItemTypeDefinition> BlockTypeAsync(string group, Guid contentKey, Guid? settingsKey, FieldDefinition[] extraFields, IElementSchemas elements, CancellationToken cancellationToken)
    {
        var content = RequireType(contentKey);
        var schema = await elements.SchemaOfAsync(contentKey, cancellationToken);

        // A block's settings element is an object field of the block, beside its own fields.
        FieldDefinition[] settings = settingsKey is { } key
            ? [FieldDefinition.ObjectOf(SettingsField, "The block's settings.", await elements.SchemaOfAsync(key, cancellationToken))]
            : [];

        return new ItemTypeDefinition(group, content.Alias, content.Name ?? content.Alias, content.Description ?? string.Empty,
            schema with { Fields = [.. schema.Fields, .. settings, .. extraFields] });
    }

    // ── Reading a block value into items ────────────────────────────────────────────────────

    public async Task<JsonNode?> ReadAsync(JsonNode stored, PropertyValueReader reader, CancellationToken cancellationToken = default)
    {
        var text = stored is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : stored.ToJsonString();
        var isGrid = IsGrid(reader);
        BlockValue? blockValue = isGrid ? json.Deserialize<BlockGridValue>(text) : json.Deserialize<BlockListValue>(text);
        if (blockValue is null)
        {
            return null;
        }

        var blocks = BlocksOf(reader.DataType);
        var contentData = blockValue.ContentData.ToDictionary(d => d.Key);
        var settingsData = blockValue.SettingsData.ToDictionary(d => d.Key);

        var items = new JsonArray();
        var layout = blockValue.Layout.TryGetValue(reader.Property.PropertyEditorAlias, out var found) ? found : [];
        foreach (var layoutItem in layout.OfType<BlockLayoutItemBase>())
        {
            items.Add(await ReadItemAsync(layoutItem, blocks, contentData, settingsData, reader));
        }

        return items;
    }

    private async Task<JsonObject> ReadItemAsync(BlockLayoutItemBase layoutItem, IReadOnlyList<BlockMap> blocks, Dictionary<Guid, BlockItemData> contentData, Dictionary<Guid, BlockItemData> settingsData, PropertyValueReader reader)
    {
        var content = contentData[layoutItem.ContentKey];
        var elementType = RequireType(content.ContentTypeKey);

        // Null when the data type no longer allows the block: it still loads, and validation reports it.
        var block = blocks.FirstOrDefault(b => b.ContentElementTypeKey == content.ContentTypeKey);

        var item = EntityValues.NewItem(content.Key, elementType.Alias);
        await reader.ReadElementAsync(item, content.ContentTypeKey, content.Values);

        if (layoutItem.SettingsKey is { } settingsKey
            && settingsData.TryGetValue(settingsKey, out var settings)
            && (block is null ? settings.ContentTypeKey : block.SettingsElementTypeKey) is { } settingsType)
        {
            item[SettingsKeyProperty] = settingsKey.ToString();
            if (block is null)
            {
                item[SettingsTypeProperty] = settingsType.ToString();
            }

            var settingsValues = new JsonObject();
            await reader.ReadElementAsync(settingsValues, settingsType, settings.Values);
            item[SettingsField] = settingsValues;
        }

        if (layoutItem is BlockGridLayoutItem gridItem)
        {
            item[LayoutField] = new JsonObject { ["columnSpan"] = gridItem.ColumnSpan, ["rowSpan"] = gridItem.RowSpan };
            foreach (var area in gridItem.Areas ?? [])
            {
                var areaItems = new JsonArray();
                foreach (var areaItem in area.Items ?? [])
                {
                    areaItems.Add(await ReadItemAsync(areaItem, blocks, contentData, settingsData, reader));
                }

                item[block?.Areas.FirstOrDefault(a => a.Key == area.Key)?.Alias ?? area.Key.ToString()] = areaItems;
            }
        }

        return item;
    }

    // ── Writing items as a block value ──────────────────────────────────────────────────────

    /// <summary>Items as Umbraco's own block value: layout, content and settings data, and which cultures expose each block.</summary>
    public async Task<JsonNode?> WriteAsync(JsonNode value, PropertyValueWriter writer, CancellationToken cancellationToken = default)
    {
        var isGrid = IsGrid(writer);
        var blocks = BlocksOf(writer.DataType);
        var written = new WrittenBlocks();
        var contentData = new List<BlockItemData>();
        var settingsData = new List<BlockItemData>();
        var expose = new List<BlockItemVariation>();

        async Task<BlockLayoutItemBase> WriteItemAsync(JsonObject item, string path)
        {
            var key = EntityValues.KeyOf(item);
            var block = blocks.FirstOrDefault(b => b.ContentAlias == EntityValues.TypeOf(item)) ?? UnconfiguredBlock(item);

            var element = await writer.WriteElementAsync<BlockPropertyValue>(item, block.ContentElementTypeKey, path);
            contentData.Add(new BlockItemData { Key = key, ContentTypeKey = block.ContentElementTypeKey, ContentTypeAlias = block.ContentAlias, Values = [.. element.Values] });
            written.Content.Add(new WrittenBlock(key, path, element));

            // An element that varies by culture is exposed in each culture it has values for (or every culture
            // of the document when it has none yet); an invariant element is exposed once.
            List<string?> exposedIn = [writer.Culture];
            if (element.VariesByCulture)
            {
                exposedIn = element.Cultures.Count > 0 ? [.. element.Cultures]
                    : writer.Culture is not null ? [writer.Culture]
                    : [.. writer.DocumentCultures];
            }

            foreach (var culture in exposedIn)
            {
                expose.Add(new BlockItemVariation(key, culture, null));
                written.Exposed.Add((key, culture));
            }

            Guid? settingsKey = null;
            if (block.SettingsElementTypeKey is { } settingsType)
            {
                settingsKey = item[SettingsKeyProperty] is { } stored ? Guid.Parse(stored.ToString()) : DerivedKey(key, "settings");
                var settingsPath = $"{path}/{SettingsField}";
                var settings = await writer.WriteElementAsync<BlockPropertyValue>(item[SettingsField] as JsonObject ?? [], settingsType, settingsPath);
                settingsData.Add(new BlockItemData { Key = settingsKey.Value, ContentTypeKey = settingsType, Values = [.. settings.Values] });
                written.Settings.Add(new WrittenBlock(key, settingsPath, settings));
            }

            if (!isGrid)
            {
                return new BlockListLayoutItem { ContentKey = key, SettingsKey = settingsKey };
            }

            var areas = new List<BlockGridLayoutAreaItem>();
            foreach (var area in block.Areas)
            {
                var areaItems = new List<BlockGridLayoutItem>();
                foreach (var areaItem in (item[area.Alias] as JsonArray ?? []).OfType<JsonObject>())
                {
                    areaItems.Add((BlockGridLayoutItem)await WriteItemAsync(areaItem, $"{path}/{area.Alias}/{EntityValues.KeyOf(areaItem)}"));
                }

                areas.Add(new BlockGridLayoutAreaItem(area.Key) { Items = [.. areaItems] });
            }

            var layout = item[LayoutField] as JsonObject;
            return new BlockGridLayoutItem
            {
                ContentKey = key,
                SettingsKey = settingsKey,
                ColumnSpan = IntOf(layout?["columnSpan"]),
                RowSpan = IntOf(layout?["rowSpan"]) ?? 1,
                Areas = [.. areas],
            };
        }

        var layoutItems = new List<IBlockLayoutItem>();
        foreach (var item in value.AsArray().OfType<JsonObject>())
        {
            layoutItems.Add(await WriteItemAsync(item, $"{writer.Path}/{EntityValues.KeyOf(item)}"));
        }

        writer.LocateErrorsWith(written.Locate);

        var blockLayout = new Dictionary<string, IEnumerable<IBlockLayoutItem>> { [writer.Property.PropertyEditorAlias] = layoutItems };
        BlockValue blockValue = isGrid
            ? new BlockGridValue { Layout = blockLayout, ContentData = contentData, SettingsData = settingsData, Expose = expose }
            : new BlockListValue { Layout = blockLayout, ContentData = contentData, SettingsData = settingsData, Expose = expose };
        return JsonNode.Parse(json.Serialize(blockValue));
    }

    // A block its data type no longer allows, so it is written back as it was read until it's removed. Its settings
    // are kept; a grid block's areas are not, since the configuration no longer names them.
    private BlockMap UnconfiguredBlock(JsonObject item)
    {
        var alias = EntityValues.TypeOf(item);
        var type = contentTypes.Get(alias) ?? throw new InvalidOperationException($"No element type '{alias}'.");
        var settingsType = item[SettingsTypeProperty] is { } stored ? Guid.Parse(stored.ToString()) : (Guid?)null;
        return new BlockMap(type.Key, alias, settingsType, []);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static bool IsGrid(PropertyEditorContext context) => context.Property.PropertyEditorAlias == Constants.PropertyEditors.Aliases.BlockGrid;

    /// <summary>The blocks the data type allows: element types, settings and (Block Grid) areas.</summary>
    private IReadOnlyList<BlockMap> BlocksOf(IDataType? dataType) => dataType?.ConfigurationObject switch
    {
        BlockGridConfiguration grid => grid.Blocks.Select(b => new BlockMap(b.ContentElementTypeKey, RequireType(b.ContentElementTypeKey).Alias, b.SettingsElementTypeKey,
            b.Areas.Select(a => new AreaMap(a.Key, a.Alias ?? a.Key.ToString())).ToList())).ToList(),
        BlockListConfiguration list => list.Blocks.Select(b => new BlockMap(b.ContentElementTypeKey, RequireType(b.ContentElementTypeKey).Alias, b.SettingsElementTypeKey, [])).ToList(),
        _ => [],
    };

    private IContentType RequireType(Guid key)
        => contentTypes.Get(key) ?? throw new InvalidOperationException($"No content type with key {key}.");

    private static int? IntOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>A stable key derived from another, so new blocks get the same settings key on every replay.</summary>
    private static Guid DerivedKey(Guid key, string purpose)
        => new(MD5.HashData(Encoding.UTF8.GetBytes($"{key:N}/{purpose}")));

    /// <summary>One configured block: its content and settings element types, and (Block Grid) its areas.</summary>
    private sealed record BlockMap(Guid ContentElementTypeKey, string ContentAlias, Guid? SettingsElementTypeKey, IReadOnlyList<AreaMap> Areas);

    private sealed record AreaMap(Guid Key, string Alias);

    /// <summary>A block element as written, at its entity path.</summary>
    private sealed record WrittenBlock(Guid ContentKey, string Path, ElementValues<BlockPropertyValue> Element);

    /// <summary>
    /// What was written into one block value, to turn Umbraco's errors inside it back into entity paths. Umbraco indexes
    /// <c>contentData[i]</c> over the blocks exposed in the culture being validated, so the lookup replays that filter. A
    /// block property inside a block continues the JSON path, so the lookup repeats one level down.
    /// </summary>
    private sealed class WrittenBlocks
    {
        public List<WrittenBlock> Content { get; } = [];

        public List<WrittenBlock> Settings { get; } = [];

        public List<(Guid Key, string? Culture)> Exposed { get; } = [];

        public ErrorLocation? Locate(string jsonPath, string? culture, string? segment)
        {
            var match = BlockValuePath.Match(jsonPath);
            if (!match.Success)
            {
                return null;
            }

            // Inside an invariant block property the culture is in the filter, not on the error.
            culture = match.Groups["culture"].Success ? match.Groups["culture"].Value : culture;
            segment = match.Groups["segment"].Success ? match.Groups["segment"].Value : segment;

            var exposed = Exposed.Where(e => e.Culture == culture || e.Culture is null).Select(e => e.Key).ToHashSet();
            var elements = (match.Groups["data"].Value == "contentData" ? Content : Settings).Where(e => exposed.Contains(e.ContentKey)).ToList();
            var i = int.Parse(match.Groups["item"].Value);
            if (i >= elements.Count)
            {
                return null;
            }

            var written = elements[i];
            var field = match.Groups["alias"].Success
                ? match.Groups["alias"].Value
                : written.Element.Values.ElementAtOrDefault(int.Parse(match.Groups["value"].Value))?.Alias;
            if (field is null)
            {
                return new ErrorLocation(written.Path, culture, segment);
            }

            // The error is inside a property of this block: let that property's handler locate it.
            var rest = jsonPath[(match.Index + match.Length)..];
            return (rest.Length > 0 ? written.Element.Errors.Locate(field, culture, segment, rest) : null)
                ?? new ErrorLocation($"{written.Path}/{field}", culture, segment);
        }
    }
}
