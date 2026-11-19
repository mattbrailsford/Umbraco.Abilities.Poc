using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.DynamicRoot;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Abilities.Cms;

/// <summary>
/// What every content-type adapter shares: the property editor handlers, the schema they build, and the item types
/// of the groups they name. Make one and give it to each adapter (documents, media, members), so each group's item
/// types are served once.
/// </summary>
public sealed class CmsContentSchemas
{
    public CmsContentSchemas(
        IContentTypeService contentTypes,
        IDataTypeService dataTypes,
        ILanguageService languages,
        IJsonSerializer json,
        PropertyEditorCollection editors,
        ItemTypeRegistry itemTypes,
        IPropertyEditorSchemaService? schemas = null,
        IEnumerable<IPropertyEditorEntityHandler>? editorHandlers = null,
        IEntityService? entities = null,
        IMediaTypeService? mediaTypes = null,
        IDynamicRootService? dynamicRoots = null)
    {
        Languages = languages;
        Json = json;
        Editors = editors;
        ItemTypes = itemTypes;

        // Packages' handlers first, so a package can map a core editor its own way; then the core editors'; then
        // every other editor as a whole JSON value.
        var asJson = new JsonValueEditorHandler(schemas);
        var handlers = new PropertyEditorHandlerCollection(
            [
                .. editorHandlers ?? [],
                .. ValueEditorHandler.Core,
                new BlockEditorHandler(contentTypes, dataTypes, json),
                new MultiNodeTreePickerHandler(entities, contentTypes, mediaTypes, dynamicRoots, asJson),
            ],
            asJson);
        Mapper = new CmsSchemaMapper(contentTypes, dataTypes, handlers);
        itemTypes.AddSource(new PropertyEditorItemTypeSource(handlers, Mapper));
    }

    public ItemTypeRegistry ItemTypes { get; }

    internal ILanguageService Languages { get; }

    internal IJsonSerializer Json { get; }

    internal PropertyEditorCollection Editors { get; }

    internal CmsSchemaMapper Mapper { get; }
}
