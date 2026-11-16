using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace Umbraco.Abilities.Cms;

internal enum PropertyKind
{
    Field,
    BlockList,
    BlockGrid,
}

/// <summary>One configured block: its content and settings element types, and (Block Grid) its areas.</summary>
internal sealed record BlockMap(Guid ContentElementTypeKey, string ContentAlias, Guid? SettingsElementTypeKey, IReadOnlyList<AreaMap> Areas);

internal sealed record AreaMap(Guid Key, string Alias);

/// <summary>How one property maps: to a field, which for a block editor is an items field of blocks.</summary>
internal sealed record PropertyMap(string Alias, string EditorAlias, PropertyKind Kind, FieldDefinition Field, IReadOnlyList<BlockMap> Blocks)
{
    public BlockMap? BlockFor(string elementAlias) => Blocks.FirstOrDefault(b => b.ContentAlias == elementAlias);
}

/// <summary>The schema of a document or element type, plus how each of its properties maps.</summary>
internal sealed record SchemaMap(ObjectSchema Schema, ContentVariation Variations, IReadOnlyDictionary<string, PropertyMap> Properties);

/// <summary>
/// Builds the schema language from Umbraco document types, element types and data types. Block editors
/// become items fields with one item type group per data type (<c>cms.blocks:{dataTypeKey}</c>), because the same
/// element type can be used by several block editors with different settings types and areas. The group's types are
/// read from the data type when they're needed (<see cref="BlockItemTypesAsync"/>), so they follow its configuration.
/// </summary>
internal sealed class CmsSchemaMapper(
    IContentTypeService contentTypes,
    IDataTypeService dataTypes,
    IPropertyEditorSchemaService? schemas)
{
    public const string NameField = "name";
    public const string ParentField = "parent";
    public const string SettingsField = "settings";
    public const string LayoutField = "layout";
    public const string BlockGroupPrefix = "cms.blocks:";

    private const string AreasSuffix = "/areas";

    private static string BlockGroup(Guid dataTypeKey) => $"{BlockGroupPrefix}{dataTypeKey}";

    private static string AreaGroup(Guid dataTypeKey) => $"{BlockGroup(dataTypeKey)}{AreasSuffix}";

    public IContentType RequireType(Guid key)
        => contentTypes.Get(key) ?? throw new InvalidOperationException($"No content type with key {key}.");

    public async Task<SchemaMap> MapAsync(IContentTypeComposition type, bool isDocument)
    {
        var fields = new List<FieldDefinition>();
        var properties = new Dictionary<string, PropertyMap>(StringComparer.Ordinal);

        if (isDocument)
        {
            fields.Add(new FieldDefinition(NameField, "The document's name.", FieldType.String, Required: true)
            {
                VariesBy = type.VariesByCulture() ? [VariationDimensions.Culture] : [],
            });
            fields.Add(new FieldDefinition(ParentField, "The parent document. Only used when creating; moving is a separate operation.", FieldType.EntityReference)
            {
                ReferencesEntityType = CmsDocumentAdapter.Type,
            });
        }

        foreach (var propertyType in type.CompositionPropertyTypes)
        {
            var map = await MapPropertyAsync(propertyType);
            properties[propertyType.Alias] = map;
            fields.Add(map.Field);
        }

        return new SchemaMap(new ObjectSchema(fields), type.Variations, properties);
    }

    private async Task<PropertyMap> MapPropertyAsync(IPropertyType propertyType)
    {
        var alias = propertyType.Alias;
        var editor = propertyType.PropertyEditorAlias;
        var description = string.IsNullOrWhiteSpace(propertyType.Description) ? propertyType.Name : propertyType.Description;
        var varies = propertyType.Variations;

        PropertyMap AsField(FieldType type, bool many = false, string? references = null)
            => new(alias, editor, PropertyKind.Field, new FieldDefinition(alias, description, type, propertyType.Mandatory)
            {
                VariesBy = CmsVariants.DimensionsOf(varies),
                Many = many,
                ReferencesEntityType = references,
            }, []);

        switch (editor)
        {
            case Constants.PropertyEditors.Aliases.TextBox:
            case Constants.PropertyEditors.Aliases.TextArea:
                return AsField(FieldType.String);
            case Constants.PropertyEditors.Aliases.Integer:
                return AsField(FieldType.Integer);
            case Constants.PropertyEditors.Aliases.Decimal:
                return AsField(FieldType.Number);
            case Constants.PropertyEditors.Aliases.Boolean:
                return AsField(FieldType.Boolean);
            case Constants.PropertyEditors.Aliases.Tags:
                return AsField(FieldType.String, many: true);
            case Constants.PropertyEditors.Aliases.ContentPicker:
                return AsField(FieldType.EntityReference, references: CmsDocumentAdapter.Type);
            case Constants.PropertyEditors.Aliases.BlockList:
            case Constants.PropertyEditors.Aliases.BlockGrid:
                return await MapBlockEditorAsync(propertyType, description, varies);
            default:
                return await MapByEditorAsync(propertyType, description, varies);
        }
    }

    /// <summary>
    /// Any other editor defines its own value model. The value is set whole, described by the editor's own value schema
    /// when it has one (<see cref="IPropertyEditorSchemaService"/>), and checked by Umbraco when saved.
    /// </summary>
    private async Task<PropertyMap> MapByEditorAsync(IPropertyType propertyType, string description, ContentVariation varies)
    {
        var editor = propertyType.PropertyEditorAlias;
        var dataType = await dataTypes.GetAsync(propertyType.DataTypeKey);
        System.Text.Json.Nodes.JsonObject? schema = null;
        try
        {
            schema = schemas?.GetValueSchema(editor, dataType?.ConfigurationObject);
        }
        catch (Exception)
        {
            // An editor whose schema can't be built is treated like one without a schema.
        }

        if (schema is null)
        {
            description = $"{description} A value in the '{editor}' editor's own format.";
        }

        return new PropertyMap(propertyType.Alias, editor, PropertyKind.Field, new FieldDefinition(propertyType.Alias, description, FieldType.Json, propertyType.Mandatory)
        {
            VariesBy = CmsVariants.DimensionsOf(varies),
            JsonSchema = schema,
        }, []);
    }

    private async Task<PropertyMap> MapBlockEditorAsync(IPropertyType propertyType, string description, ContentVariation varies)
    {
        var dataType = await dataTypes.GetAsync(propertyType.DataTypeKey)
            ?? throw new InvalidOperationException($"No data type {propertyType.DataTypeKey} for property '{propertyType.Alias}'.");
        var isGrid = propertyType.PropertyEditorAlias == Constants.PropertyEditors.Aliases.BlockGrid;

        // How the value maps: which element types, settings and areas each configured block has. The item types, with
        // the elements' fields, come from BlockItemTypesAsync when a call needs them.
        List<BlockMap> blockMaps;
        (int? Min, int? Max) limit;
        if (isGrid)
        {
            var grid = dataType.ConfigurationObject as BlockGridConfiguration ?? new BlockGridConfiguration();
            blockMaps = grid.Blocks.Select(b => new BlockMap(b.ContentElementTypeKey, RequireType(b.ContentElementTypeKey).Alias, b.SettingsElementTypeKey,
                b.Areas.Select(a => new AreaMap(a.Key, a.Alias ?? a.Key.ToString())).ToList())).ToList();
            limit = (grid.ValidationLimit.Min, grid.ValidationLimit.Max);
        }
        else
        {
            var list = dataType.ConfigurationObject as BlockListConfiguration ?? new BlockListConfiguration();
            blockMaps = list.Blocks.Select(b => new BlockMap(b.ContentElementTypeKey, RequireType(b.ContentElementTypeKey).Alias, b.SettingsElementTypeKey, [])).ToList();
            limit = (list.ValidationLimit.Min, list.ValidationLimit.Max);
        }

        var field = FieldDefinition.ItemsOf(propertyType.Alias, description, BlockGroup(dataType.Key)) with
        {
            VariesBy = CmsVariants.DimensionsOf(varies),
            Min = limit.Min ?? (propertyType.Mandatory ? 1 : null),
            Max = limit.Max,
        };

        return new PropertyMap(propertyType.Alias, propertyType.PropertyEditorAlias, isGrid ? PropertyKind.BlockGrid : PropertyKind.BlockList, field, blockMaps);
    }

    /// <summary>
    /// The item types of a block group: the blocks its data type allows now. A Block List's group holds every configured
    /// block. A Block Grid has two: the root (blocks allowed at the root, or not in areas) and its areas (blocks allowed
    /// in areas). Each block's fields are its element type's, plus its settings and, in a grid, its layout and areas.
    /// </summary>
    public async Task<IEnumerable<ItemTypeDefinition>> BlockItemTypesAsync(string group)
    {
        var name = group[BlockGroupPrefix.Length..];
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
                var areas = block.Areas.Select(a => FieldDefinition.ItemsOf(a.Alias ?? a.Key.ToString(), $"Area '{a.Alias}'.", AreaGroup(dataTypeKey)) with
                {
                    Min = a.MinAllowed,
                    Max = a.MaxAllowed,
                });
                types.Add(await BlockTypeAsync(group, block.ContentElementTypeKey, block.SettingsElementTypeKey, [.. areas, layout]));
            }
        }
        else if (dataType?.ConfigurationObject is BlockListConfiguration list && !inAreas)
        {
            foreach (var block in list.Blocks)
            {
                types.Add(await BlockTypeAsync(group, block.ContentElementTypeKey, block.SettingsElementTypeKey, []));
            }
        }

        return types;
    }

    private async Task<ItemTypeDefinition> BlockTypeAsync(string group, Guid contentKey, Guid? settingsKey, FieldDefinition[] extraFields)
    {
        var content = RequireType(contentKey);
        var schema = (await MapAsync(content, isDocument: false)).Schema;

        // A block's settings element is an object field of the block, beside its own fields.
        FieldDefinition[] settings = settingsKey is { } key
            ? [FieldDefinition.ObjectOf(SettingsField, "The block's settings.", (await MapAsync(RequireType(key), isDocument: false)).Schema)]
            : [];

        return new ItemTypeDefinition(group, content.Alias, content.Name ?? content.Alias, content.Description ?? string.Empty,
            schema with { Fields = [.. schema.Fields, .. settings, .. extraFields] });
    }
}

/// <summary>Every CMS block group's item types, read from its data type each call, so they follow its configuration.</summary>
internal sealed class CmsBlockItemTypeSource(CmsSchemaMapper mapper) : IItemTypeSource
{
    public bool Serves(string group) => group.StartsWith(CmsSchemaMapper.BlockGroupPrefix, StringComparison.Ordinal);

    public Task<IEnumerable<ItemTypeDefinition>> GetItemTypesAsync(string group, CancellationToken cancellationToken = default)
        => mapper.BlockItemTypesAsync(group);
}
