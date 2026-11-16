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
/// Builds the schema language from Umbraco document types and element types. A document has a name and a parent; every
/// property is described by its editor's handler (<see cref="IPropertyEditorEntityHandler"/>), so the mapper knows no
/// editor. Only the property's alias and variation are the CMS's, and set here.
/// </summary>
internal sealed class CmsSchemaMapper(
    IContentTypeService contentTypes,
    IDataTypeService dataTypes,
    PropertyEditorHandlerCollection handlers) : IElementSchemas
{
    public const string NameField = "name";
    public const string ParentField = "parent";

    public IContentType RequireType(Guid key)
        => contentTypes.Get(key) ?? throw new InvalidOperationException($"No content type with key {key}.");

    public async Task<ObjectSchema> SchemaOfAsync(Guid elementTypeKey, CancellationToken cancellationToken = default)
        => (await MapAsync(RequireType(elementTypeKey), isDocument: false, cancellationToken)).Schema;

    public async Task<SchemaMap> MapAsync(IContentTypeComposition type, bool isDocument, CancellationToken cancellationToken = default)
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

        // Whatever the handler made, the field is the property: its alias, and the variation the property has.
        return new PropertyMap(context, handler, field with
        {
            Alias = propertyType.Alias,
            VariesBy = CmsVariants.DimensionsOf(propertyType.Variations),
        });
    }
}
