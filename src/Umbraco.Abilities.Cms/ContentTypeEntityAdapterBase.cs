using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.DynamicRoot;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.ContentEditing.Validation;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Serialization;
using Umbraco.Extensions;

namespace Umbraco.Abilities.Cms;

/// <summary>The fields every content-type entity has besides its properties.</summary>
public static class ContentFields
{
    /// <summary>The name, per culture when the type varies by culture.</summary>
    public const string Name = "name";

    /// <summary>The parent. For an adapter that is an <see cref="ITreeEntityAdapter"/>, changing it moves the item; otherwise it is only used when creating.</summary>
    public const string Parent = "parent";
}

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

/// <summary>A content item as stored, in editor (Management API) format: what a content-type adapter loads.</summary>
/// <param name="Key">The item's key.</param>
/// <param name="TypeKey">Its content type's key.</param>
/// <param name="Names">Its name by culture, with <c>""</c> for an invariant name.</param>
/// <param name="Values">Its property values, one per property, culture and segment.</param>
/// <param name="Version">Its version, to tell an existing item from a new one.</param>
/// <param name="ParentKey">
/// Its parent: for a new item not saved yet (a draft), so the parent survives being read back and edited again, and for
/// a saved item whose adapter can move it. Null at the root, and for a saved item that can't be moved.
/// </param>
public sealed record StoredContent(Guid Key, Guid TypeKey, IReadOnlyDictionary<string, string> Names, IReadOnlyList<StoredValue> Values, long? Version, Guid? ParentKey = null);

/// <summary>One stored property value, in editor format.</summary>
public sealed record StoredValue(string Alias, string? Culture, string? Segment, JsonNode? Value);

/// <summary>
/// An entity as the CMS's editing services take it: names as variants, property values in editor format, and how to
/// turn the CMS's validation errors back into entity paths.
/// </summary>
public sealed class ContentValues
{
    internal ContentValues(Guid key, Guid typeKey, Guid? parentKey, List<VariantModel> variants, List<StoredValue> values, PropertyErrorLocators errors)
    {
        Key = key;
        TypeKey = typeKey;
        ParentKey = parentKey;
        Variants = variants;
        Values = values;
        Errors = errors;
    }

    private static readonly JsonSerializerOptions ModelValueOptions = new() { Converters = { new JsonObjectConverter() } };

    public Guid Key { get; }

    public Guid TypeKey { get; }

    public Guid? ParentKey { get; }

    /// <summary>The names, one variant per culture and segment that has values.</summary>
    public List<VariantModel> Variants { get; }

    /// <summary>The property values in editor format.</summary>
    public List<StoredValue> Values { get; }

    /// <summary>The property values as the editing services take them.</summary>
    public List<PropertyValueModel> Properties
        => Values.Select(v => new PropertyValueModel { Alias = v.Alias, Value = ToModelValue(v.Value), Culture = v.Culture, Segment = v.Segment }).ToList();

    /// <summary>The cultures the item has, to validate only those: validating others fails values that don't exist there.</summary>
    public ISet<string>? Cultures => Variants.Any(v => v.Culture is not null) ? Variants.Select(v => v.Culture).OfType<string>().ToHashSet() : null;

    internal PropertyErrorLocators Errors { get; }

    /// <summary>A CMS validation error as a problem on the entity path the value came from.</summary>
    public Problem ToProblem(PropertyValidationError error)
    {
        // Same fields as Umbraco's error; only the alias and JSON path become one entity path.
        var located = new Problem(error.Alias, error.ErrorMessages.ToList()).In(CmsVariants.Of(error.Culture, error.Segment));
        if (string.IsNullOrEmpty(error.JsonPath) || error.JsonPath == "$")
        {
            return located;
        }

        return Errors.Locate(error.Alias, error.Culture, error.Segment, error.JsonPath) is { } at
            ? located with { Path = at.Path, Variant = CmsVariants.Of(at.Culture, at.Segment).ToJson() }
            : located with { Path = $"{error.Alias} {error.JsonPath}" };
    }

    internal static object? ToModelValue(JsonNode? node)
        => node is null ? null : JsonSerializer.Deserialize<object?>(node.ToJsonString(), ModelValueOptions);
}

/// <summary>
/// The base for an entity whose types are data: defined by editors at run time, with properties whose values are
/// shaped by their property editors. Documents, media and members are all this, over Umbraco's content types.
/// <para>
/// The base builds the schema from the type (a name, a parent, then each property as its editor's handler describes
/// it), reads stored values into an entity and writes an entity back as the editing services take it, maps the CMS's
/// validation errors to entity paths, and finds the values an edit touched. The product lists and finds its types,
/// loads and saves through its own services, and checks access with its own permissions.
/// </para>
/// </summary>
/// <typeparam name="TType">The product's content type: <c>IContentType</c>, <c>IMediaType</c> or <c>IMemberType</c>.</typeparam>
public abstract class ContentTypeEntityAdapterBase<TType>(CmsContentSchemas schemas) : IAuthorizingEntityAdapter
    where TType : class, IContentTypeComposition
{
    protected CmsContentSchemas Schemas { get; } = schemas;

    public abstract string EntityType { get; }

    /// <summary>What a person calls this entity type, such as "Document".</summary>
    public abstract string DisplayName { get; }

    public virtual bool CanCreate => true;

    public abstract IUnitOfWorkProvider UnitOfWork { get; }

    /// <summary>The types an entity can be created as.</summary>
    protected abstract IEnumerable<TType> ListTypes();

    /// <summary>A type by alias, or null.</summary>
    protected abstract TType? FindType(string alias);

    /// <summary>A type by key, or null.</summary>
    protected abstract TType? FindType(Guid key);

    /// <summary>Whether the entity has a parent field. True for documents and media; members have none.</summary>
    protected virtual bool HasParent => true;

    /// <summary>Where the item is edited in the backoffice.</summary>
    protected abstract string EditUrl(Guid key);

    /// <summary>The item as stored, in editor format, or null if there is none. <see cref="ReadStored"/> reads one from the item.</summary>
    protected abstract Task<StoredContent?> LoadStoredAsync(Guid key, CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<Problem>> ValidateAsync(Entity entity, CancellationToken cancellationToken = default);

    public abstract Task<EntitySummary> SaveAsync(Entity? before, Entity entity, CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default);

    public abstract Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default);

    // ── Types and schema ────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<EntitySubtype>> GetSubtypesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySubtype>>(ListTypes().Select(t => new EntitySubtype(t.Alias, t.Name ?? t.Alias)).ToList());

    public async Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default)
    {
        var type = RequireType(subtype);
        var map = await MapAsync(type, cancellationToken);
        var cultures = (await Schemas.Languages.GetAllAsync()).Select(l => l.IsoCode).ToList();
        return new EntitySchema(EntityType, type.Name ?? type.Alias, type.Description ?? string.Empty, map.Schema)
        {
            // Listed for every type: blocks inside an invariant item can still vary.
            Dimensions =
            [
                new VariationDimension(VariationDimensions.Culture, "The site's languages.", cultures),
                new VariationDimension(VariationDimensions.Segment, "A segment of the audience. Leave it out for the default."),
            ],
        };
    }

    public Task<Entity> NewAsync(Guid key, string? subtype, CancellationToken cancellationToken = default)
        => Task.FromResult(new Entity { EntityType = EntityType, Key = key, Subtype = subtype });

    public async Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default)
        => await LoadStoredAsync(key, cancellationToken) is { } stored ? await ToEntityAsync(stored, cancellationToken) : null;

    public virtual EntitySummary ToSummary(Entity entity)
        => new(EntityType, entity.Key, EntityValues.ReadAny(entity.Fields, ContentFields.Name)?.GetValue<string>() ?? "(unnamed)", EditUrl(entity.Key), entity.Subtype);

    /// <summary>A type by alias, or a problem on the subtype.</summary>
    protected TType RequireType(string? alias)
        => FindType(alias ?? string.Empty)
            ?? throw new AbilityInputException($"No {EntityType} type '{alias}'.", [new Problem("subtype", $"Not a {EntityType} type.")]);

    private Task<SchemaMap> MapAsync(TType type, CancellationToken cancellationToken)
    {
        List<FieldDefinition> fields =
        [
            new FieldDefinition(ContentFields.Name, $"The {EntityType}'s name.", FieldType.String, Required: true)
            {
                VariesBy = type.VariesByCulture() ? [VariationDimensions.Culture] : [],
            },
        ];
        if (HasParent)
        {
            var description = this is ITreeEntityAdapter
                ? $"The parent {EntityType}; none at the root. To move an existing one, use entity.move."
                : $"The parent {EntityType}. Only used when creating.";
            fields.Add(new FieldDefinition(ContentFields.Parent, description, FieldType.EntityReference)
            {
                ReferencesEntityType = EntityType,
            });
        }

        return Schemas.Mapper.MapAsync(type, fields, cancellationToken);
    }

    // ── Reading stored values ───────────────────────────────────────────────────────────────

    /// <summary>An item as stored, in editor format, as the backoffice loads it: each value through its editor's <c>ToEditor</c>.</summary>
    protected StoredContent ReadStored(IContentBase content)
    {
        var names = content.ContentType.VariesByCulture()
            ? content.AvailableCultures.ToDictionary(c => c, c => content.GetCultureName(c) ?? string.Empty)
            : new Dictionary<string, string> { [string.Empty] = content.Name ?? string.Empty };

        var values = new List<StoredValue>();
        foreach (var property in content.Properties)
        {
            foreach (var value in property.Values)
            {
                var editorValue = Schemas.Editors.TryGet(property.PropertyType.PropertyEditorAlias, out var editor)
                    ? editor.GetValueEditor().ToEditor(property, value.Culture, value.Segment)
                    : property.GetValue(value.Culture, value.Segment);
                if (ToNode(editorValue) is { } node)
                {
                    values.Add(new StoredValue(property.Alias, value.Culture, value.Segment, node));
                }
            }
        }

        return new StoredContent(content.Key, content.ContentType.Key, names, values, content.UpdateDate.Ticks);
    }

    /// <summary>A stored item as an entity: its names, then each property's value read by its editor's handler.</summary>
    protected async Task<Entity> ToEntityAsync(StoredContent content, CancellationToken cancellationToken = default)
    {
        var type = FindType(content.TypeKey) ?? throw new InvalidOperationException($"No {EntityType} type with key {content.TypeKey}.");
        var map = await MapAsync(type, cancellationToken);
        var entity = new Entity { EntityType = EntityType, Key = content.Key, Subtype = type.Alias, Version = content.Version };

        var nameField = map.Schema.Find(ContentFields.Name)!;
        foreach (var (culture, name) in content.Names)
        {
            EntityValues.Set(entity.Fields, nameField, CmsVariants.Of(culture.Length == 0 ? null : culture, null), name);
        }

        if (HasParent && content.ParentKey is { } parent)
        {
            entity.Fields[ContentFields.Parent] = parent.ToString();
        }

        await ReadValuesAsync(entity.Fields, content.Values.Select(v => (v.Alias, v.Culture, v.Segment, v.Value)), map);
        return entity;
    }

    /// <summary>Property values (an item's or an element's) into an object, each by its editor's handler.</summary>
    private async Task ReadValuesAsync(JsonObject owner, IEnumerable<(string Alias, string? Culture, string? Segment, JsonNode? Value)> values, SchemaMap map)
    {
        foreach (var (alias, culture, segment, value) in values)
        {
            if (!map.Properties.TryGetValue(alias, out var property) || value is null)
            {
                continue;
            }

            var reader = new PropertyValueReader(property.Context, property.Field, ReadElementAsync);
            if (await property.Handler.ReadAsync(value, reader) is not { } read)
            {
                continue;
            }

            var variant = CmsVariants.Of(culture, segment);
            if (!property.Field.IsItems)
            {
                EntityValues.Set(owner, property.Field, variant, read);
                continue;
            }

            var items = EntityValues.Items(owner, property.Field, variant, create: true)!;
            var readItems = read.AsArray();
            var moved = readItems.ToList();
            readItems.Clear();
            foreach (var item in moved)
            {
                items.Add(item);
            }
        }
    }

    private async Task ReadElementAsync(JsonObject into, Guid elementTypeKey, IEnumerable<ValueModelBase> values)
        => await ReadValuesAsync(into, values.Select(v => (v.Alias, v.Culture, v.Segment, ToNode(v.Value))), await Schemas.Mapper.MapAsync(Schemas.Mapper.RequireType(elementTypeKey), [], default));

    private JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        _ => JsonNode.Parse(Schemas.Json.Serialize(value)),
    };

    // ── Writing an entity as the editing services take it ───────────────────────────────────

    /// <summary>An entity as the editing services take it: its names as variants, each value written by its editor's handler.</summary>
    protected async Task<ContentValues> ToContentAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var type = RequireType(entity.Subtype);
        var map = await MapAsync(type, cancellationToken);
        var root = entity.Fields;
        var cultures = EntityValues.VariantsIn(root, map.Schema, await Schemas.ItemTypes.ResolveAsync(map.Schema, null, cancellationToken)).Select(v => v.Culture).OfType<string>().Distinct().ToList();
        var nameField = map.Schema.Find(ContentFields.Name)!;

        var variants = new List<VariantModel>();
        if (type.VariesByCulture())
        {
            foreach (var culture in cultures)
            {
                if (EntityValues.Get(root, nameField, CmsVariants.Of(culture, null)) is { } name)
                {
                    variants.Add(new VariantModel { Culture = culture, Name = name.GetValue<string>() });
                }
            }
        }
        else
        {
            variants.Add(new VariantModel { Name = EntityValues.Get(root, nameField, VariantId.Invariant)?.GetValue<string>() ?? string.Empty });
        }

        // Umbraco has no managed segments: it validates the segments the model's variants name, in each culture with
        // values in that segment. So name every segment the item's own values are in, as the backoffice does.
        if (type.VariesBySegment())
        {
            // A value that varies by segment only has no culture, but a culture-variant item's variants all need one
            // (ContentEditingServiceBase refuses a variant without): name its segment in each of the item's cultures.
            var segments = EntityValues.OwnVariants(root, map.Schema)
                .Where(v => v.Segment is not null)
                .SelectMany(v => v.Culture is null && type.VariesByCulture() ? cultures.Select(c => CmsVariants.Of(c, v.Segment)) : [CmsVariants.Of(v.Culture, v.Segment)])
                .Distinct();
            foreach (var segmented in segments)
            {
                var name = variants.FirstOrDefault(v => v.Culture == segmented.Culture)?.Name ?? variants.FirstOrDefault()?.Name ?? string.Empty;
                variants.Add(new VariantModel { Culture = segmented.Culture, Segment = segmented.Segment, Name = name });
            }
        }

        var errors = new PropertyErrorLocators();
        var values = new List<StoredValue>();
        foreach (var property in map.Properties.Values)
        {
            foreach (var (variant, value) in Values(root, property))
            {
                var node = await ToNodeAsync(value, property, variant, cultures, variant.Culture, property.Alias, errors);
                values.Add(new StoredValue(property.Alias, variant.Culture, variant.Segment, node));
            }
        }

        var parent = root[ContentFields.Parent] is { } parentNode && Guid.TryParse(parentNode.ToString(), out var parentKey) ? parentKey : (Guid?)null;
        return new ContentValues(entity.Key, type.Key, parent, variants, values, errors);
    }

    /// <summary>
    /// The values an edit touched: the property (or the name) and variant of each change. A change anywhere inside a
    /// block property is a change to that property's value in its own variant. For products that stage per value.
    /// </summary>
    protected async Task<IReadOnlyCollection<(string Alias, string? Culture, string? Segment)>> TouchedValuesAsync(Entity before, Entity after, CancellationToken cancellationToken = default)
    {
        var type = RequireType(after.Subtype);
        var map = await MapAsync(type, cancellationToken);
        var touched = new HashSet<(string Alias, string? Culture, string? Segment)>();
        foreach (var change in EntityDiff.Compare(before.Fields, after.Fields, map.Schema, await Schemas.ItemTypes.ResolveAsync(map.Schema, null, cancellationToken)))
        {
            var alias = change.Path.Split('/')[0];
            if (alias == ContentFields.Name)
            {
                touched.Add((ContentFields.Name, type.VariesByCulture() ? change.Variant.Culture : null, null));
            }
            else if (map.Properties.TryGetValue(alias, out var property))
            {
                var variant = change.Variant.Narrow(property.Field.VariesBy);
                touched.Add((alias, variant.Culture, variant.Segment));
            }
        }

        return touched;
    }

    /// <summary>A property's values in the entity: once if invariant, else once per variant that has one.</summary>
    private static IEnumerable<(VariantId Variant, JsonNode Value)> Values(JsonObject owner, PropertyMap property)
        => EntityValues.Values(owner, property.Field).Where(v => property.Field.IsItems || v.Value is not JsonArray { Count: 0 });

    /// <summary>A field's value in editor format, by the property's editor handler, which also says how to locate errors in it.</summary>
    private async Task<JsonNode?> ToNodeAsync(JsonNode value, PropertyMap property, VariantId variant, IReadOnlyList<string> cultures, string? culture, string path, PropertyErrorLocators errors)
    {
        var writer = new PropertyValueWriter(property.Context, property.Field, path, culture, cultures,
            locator => errors.Add(property.Alias, variant, locator),
            (from, elementTypeKey, elementPath, elementCulture) => WriteElementAsync(from, elementTypeKey, elementPath, elementCulture, cultures));
        return await property.Handler.WriteAsync(value, writer);
    }

    /// <summary>An element's values (a block's, or its settings) in editor format, for a handler whose values hold elements.</summary>
    private async Task<WrittenElement> WriteElementAsync(JsonObject from, Guid elementTypeKey, string path, string? culture, IReadOnlyList<string> cultures)
    {
        var map = await Schemas.Mapper.MapAsync(Schemas.Mapper.RequireType(elementTypeKey), [], default);
        var errors = new PropertyErrorLocators();
        var values = new List<ElementValue>();
        foreach (var property in map.Properties.Values)
        {
            foreach (var (variant, value) in Values(from, property))
            {
                var node = await ToNodeAsync(value, property, variant, cultures, variant.Culture ?? culture, $"{path}/{property.Alias}", errors);
                values.Add(new ElementValue(property.Alias, variant.Culture, variant.Segment, ContentValues.ToModelValue(node)));
            }
        }

        var variesByCulture = map.Variations.VariesByCulture();
        var elementCultures = variesByCulture ? EntityValues.OwnVariants(from, map.Schema).Select(v => v.Culture).OfType<string>().Distinct().ToList() : [];
        return new WrittenElement(values, errors, variesByCulture, elementCultures);
    }
}
