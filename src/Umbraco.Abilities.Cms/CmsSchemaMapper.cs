using System.Text.Json.Nodes;

using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace Umbraco.Abilities.Cms;

/// <summary>How one property maps: its editor's handler, and the field the handler made of it.</summary>
internal sealed record PropertyMap(PropertyEditorContext Context, IPropertyEditorEntityHandler Handler, FieldDefinition Field)
{
    public string Alias => Context.Property.Alias;
}

/// <summary>The schema of a document or element type, plus how each of its properties maps.</summary>
internal sealed record SchemaMap(ObjectSchema Schema, ContentVariation Variations, IReadOnlyDictionary<string, PropertyMap> Properties);

/// <summary>
/// Builds the schema language from Umbraco content types (document, media, member and element types). Every property
/// is described by its editor's handler (<see cref="IPropertyEditorEntityHandler"/>), so the mapper knows no editor.
/// Only the property's alias and variation are the CMS's, and set here.
/// </summary>
internal sealed class CmsSchemaMapper(
    IContentTypeService contentTypes,
    IDataTypeService dataTypes,
    PropertyEditorHandlerCollection handlers) : IElementSchemas
{
    /// <summary>The metadata key naming a field's data type: its key and its editor's schema alias.</summary>
    public const string DataTypeMetaKey = "umbraco.cms.data-type";

    public IContentType RequireType(Guid key)
        => contentTypes.Get(key) ?? throw new InvalidOperationException($"No content type with key {key}.");

    public async Task<ObjectSchema> SchemaOfAsync(Guid elementTypeKey, CancellationToken cancellationToken = default)
        => (await MapAsync(RequireType(elementTypeKey), [], cancellationToken)).Schema;

    /// <summary>A type's schema: the <paramref name="leading"/> fields (a name, a parent), then each property as its handler describes it.</summary>
    public async Task<SchemaMap> MapAsync(IContentTypeComposition type, IReadOnlyList<FieldDefinition> leading, CancellationToken cancellationToken = default)
    {
        var fields = new List<FieldDefinition>(leading);
        var properties = new Dictionary<string, PropertyMap>(StringComparer.Ordinal);

        foreach (var propertyType in type.CompositionPropertyTypes)
        {
            var map = await MapPropertyAsync(propertyType, cancellationToken);
            properties[propertyType.Alias] = map;
            fields.Add(map.Field);
        }

        return new SchemaMap(new ObjectSchema(fields), type.Variations, properties);
    }

    private async Task<PropertyMap> MapPropertyAsync(IPropertyType propertyType, CancellationToken cancellationToken)
    {
        var context = new PropertyEditorContext(propertyType, await dataTypes.GetAsync(propertyType.DataTypeKey));
        var handler = handlers.For(propertyType.PropertyEditorAlias);
        var field = await handler.DescribeAsync(context, cancellationToken);

        // Whatever the handler made, the field is the property: its alias, and the variation the property has. A UI
        // gets the data type's own editor, and the data type's key to load its configuration, as the backoffice does.
        return new PropertyMap(context, handler, field with
        {
            Alias = propertyType.Alias,
            VariesBy = CmsVariants.DimensionsOf(propertyType.Variations),
            Editor = field.Editor ?? (context.DataType?.EditorUiAlias is { Length: > 0 } editorUi ? new FieldEditor(editorUi) : null),
            Meta = context.DataType is { } dataType
                ? UiHints.WithMeta(field.Meta, DataTypeMetaKey, new JsonObject { ["key"] = dataType.Key, ["editorAlias"] = dataType.EditorAlias })
                : field.Meta,
        });
    }
}
