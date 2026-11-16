using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.ContentEditing.Validation;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.DraftSets;
using Umbraco.Cms.Infrastructure.Serialization;
using Umbraco.Extensions;

namespace Umbraco.Abilities.Cms;

/// <summary>
/// The real entity adapter for CMS documents: one entity type, <c>document</c> (the UDI entity type), whose subtypes are
/// the document types. It reads through <see cref="IContentEditingService"/> in the editor (Management API) format, maps
/// each property's value to a field and back through its editor's handler (<see cref="IPropertyEditorEntityHandler"/>),
/// and validates with Umbraco's own validation.
/// <para>
/// It is also the adapter to CMS Draft Sets: inside a change set, content changes are staged into a CMS draft set (per
/// property) instead of being kept and replayed by the change set, so the draft set is the source of truth for content,
/// and its own rebasing, review and apply (all or nothing, in one Umbraco scope) are used.
/// </para>
/// </summary>
public sealed class CmsDocumentAdapter : IDraftingEntityAdapter, IAuthorizingEntityAdapter
{
    public const string Type = Constants.UdiEntityType.Document;

    private static readonly JsonSerializerOptions ModelValueOptions = new() { Converters = { new JsonObjectConverter() } };

    private readonly IContentTypeService _contentTypes;
    private readonly ILanguageService _languages;
    private readonly IContentEditingService _editing;
    private readonly IEntityService _entities;
    private readonly DraftSetService _draftSets;
    private readonly IUserService _users;
    private readonly IContentPermissionService _permissions;
    private readonly IJsonSerializer _json;
    private readonly Func<Guid> _actingUser;
    private readonly CmsSchemaMapper _mapper;
    private readonly ItemTypeRegistry _itemTypes;

    public CmsDocumentAdapter(
        IContentTypeService contentTypes,
        IDataTypeService dataTypes,
        ILanguageService languages,
        IContentEditingService editing,
        IEntityService entities,
        DraftSetService draftSets,
        IUserService users,
        IContentPermissionService permissions,
        IJsonSerializer json,
        ItemTypeRegistry itemTypes,
        IUnitOfWorkProvider unitOfWork,
        Func<Guid> actingUser,
        IPropertyEditorSchemaService? schemas = null,
        IEnumerable<IPropertyEditorEntityHandler>? editorHandlers = null)
    {
        UnitOfWork = unitOfWork;
        _contentTypes = contentTypes;
        _languages = languages;
        _editing = editing;
        _entities = entities;
        _draftSets = draftSets;
        _users = users;
        _permissions = permissions;
        _json = json;
        _actingUser = actingUser;
        _itemTypes = itemTypes;

        // Packages' handlers first, so a package can map a core editor its own way; then the core editors'; then
        // every other editor as a whole JSON value.
        var handlers = new PropertyEditorHandlerCollection(
            [.. editorHandlers ?? [], .. ValueEditorHandler.Core, new BlockEditorHandler(contentTypes, dataTypes, json)],
            new JsonValueEditorHandler(schemas));
        _mapper = new CmsSchemaMapper(contentTypes, dataTypes, handlers);
        itemTypes.AddSource(new PropertyEditorItemTypeSource(handlers, _mapper));
    }

    public string EntityType => Type;

    public bool CanCreate => true;

    public IUnitOfWorkProvider UnitOfWork { get; }

    public Task<IReadOnlyList<EntitySubtype>> GetSubtypesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySubtype>>(_contentTypes.GetAll()
            .Where(t => !t.IsElement)
            .Select(t => new EntitySubtype(t.Alias, t.Name ?? t.Alias))
            .ToList());

    public async Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default)
    {
        var type = DocumentType(subtype);
        var map = await _mapper.MapAsync(type, isDocument: true);
        var cultures = (await _languages.GetAllAsync()).Select(l => l.IsoCode).ToList();
        return new EntitySchema(Type, type.Name ?? type.Alias, type.Description ?? string.Empty, map.Schema)
        {
            // Listed for every document type: blocks inside an invariant document can still vary.
            Dimensions =
            [
                new VariationDimension(VariationDimensions.Culture, "The site's languages.", cultures),
                new VariationDimension(VariationDimensions.Segment, "A segment of the audience. Leave it out for the default."),
            ],
        };
    }

    public Task<Entity> NewAsync(Guid key, string? subtype, CancellationToken cancellationToken = default)
        => Task.FromResult(new Entity { EntityType = Type, Key = key, Subtype = subtype });

    public async Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default)
        => await _draftSets.GetLiveAsync(EntityUdis.For(Type, key)) is { } live ? await ToEntityAsync(live) : null;

    /// <summary>A document in editor format, live or as a draft set would leave it, as an entity.</summary>
    private async Task<Entity> ToEntityAsync(DraftContent content)
    {
        var type = _mapper.RequireType(content.ContentTypeKey);
        var map = await _mapper.MapAsync(type, isDocument: true);
        var document = new Entity
        {
            EntityType = Type,
            Key = content.Key,
            Subtype = type.Alias,
            Version = content.Version,
        };

        var nameField = map.Schema.Find(CmsSchemaMapper.NameField)!;
        foreach (var (culture, name) in content.Names)
        {
            EntityValues.Set(document.Fields, nameField, CmsVariants.Of(culture.Length == 0 ? null : culture, null), name);
        }

        if (content.ParentKey is { } parent)
        {
            document.Fields[CmsSchemaMapper.ParentField] = parent.ToString();
        }

        await ReadValuesAsync(document.Fields, content.Values.Select(v => (v.Alias, v.Culture, v.Segment, v.Value)), map);
        return document;
    }

    public async Task<IReadOnlyList<Problem>> ValidateAsync(Entity document, CancellationToken cancellationToken = default)
    {
        var model = await BuildModelAsync(document);
        var attempt = document.Version is null
            ? await _editing.ValidateCreateAsync(model.ToCreateModel(), _actingUser())
            : await _editing.ValidateUpdateAsync(document.Key, model.ToValidateUpdateModel(), _actingUser());

        var problems = attempt.Result.ValidationErrors.Select(model.Index.ToProblem).ToList();
        if (!attempt.Success && attempt.Status != ContentEditingOperationStatus.PropertyValidationError)
        {
            problems.Add(new Problem(string.Empty, $"Umbraco refused the document: {attempt.Status}."));
        }

        return problems;
    }

    /// <summary>Not used: content in a change set is applied through its draft set (<see cref="ApplyDraftAsync"/>).</summary>
    public Task<EntitySummary> SaveAsync(Entity document, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Content is applied through its CMS draft set.");

    public Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySummary>>(_entities.GetAll(UmbracoObjectTypes.Document)
            .Where(e => e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new EntitySummary(Type, e.Key, e.Name!, EditUrl(e.Key)))
            .ToList());

    public EntitySummary ToSummary(Entity document)
        => new(Type, document.Key, EntityValues.ReadAny(document.Fields, CmsSchemaMapper.NameField)?.GetValue<string>() ?? "(unnamed)", EditUrl(document.Key));

    // ── Authorization: the CMS's own permission model ─────────────────────────────────────────────

    /// <summary>
    /// The backoffice's checks, through the CMS's own services: the Content section, the document permission on the
    /// document (or, for a new one, its parent or the root), and access to the cultures the change touches.
    /// </summary>
    public async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _users.GetAsync(request.UserKey);
        if (user is null)
        {
            return "Unknown user.";
        }

        if (!user.AllowedSections.Contains(Constants.Applications.Content))
        {
            return "You don't have access to the Content section.";
        }

        var (permission, verb) = request.Permission switch
        {
            EntityPermission.Read => (ActionBrowse.ActionLetter, "read"),
            EntityPermission.Create => (ActionNew.ActionLetter, "create"),
            _ => (ActionUpdate.ActionLetter, "update"),
        };

        ContentAuthorizationStatus status;
        if (request.Permission == EntityPermission.Create)
        {
            var parent = request.Entity?.Fields[CmsSchemaMapper.ParentField] is { } node && Guid.TryParse(node.ToString(), out var key) ? key : (Guid?)null;
            status = parent is { } parentKey
                ? await _permissions.AuthorizeAccessAsync(user, parentKey, permission)
                : await _permissions.AuthorizeRootAccessAsync(user, permission);
        }
        else
        {
            status = await _permissions.AuthorizeAccessAsync(user, request.Key, permission);
        }

        if (status != ContentAuthorizationStatus.Success)
        {
            return status == ContentAuthorizationStatus.NotFound ? "Not found." : $"You don't have permission to {verb} this document.";
        }

        var cultures = request.Variants.Select(v => v.Culture).OfType<string>().ToHashSet();
        if (cultures.Count > 0
            && await _permissions.AuthorizeCultureAccessAsync(user, cultures) != ContentAuthorizationStatus.Success)
        {
            return $"You don't have access to edit {string.Join(", ", cultures)}.";
        }

        return null;
    }

    // ── CMS Draft Sets: the change set hands content changes to a CMS draft set ─────────────────────

    public Task<Guid> CreateDraftAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_draftSets.Create(name).Id);

    public async Task<Entity?> LoadDraftAsync(Guid draftId, Guid key, CancellationToken cancellationToken = default)
        => await _draftSets.GetAsync(draftId, EntityUdis.For(Type, key)) is { } content ? await ToEntityAsync(content) : null;

    /// <summary>
    /// Stages an edit. New content is staged whole. For existing content only the values the edit changed are staged,
    /// per property, culture and segment, so the draft set can rebase onto values others change meanwhile.
    /// </summary>
    public async Task StageAsync(Guid draftId, Entity? before, Entity after, CancellationToken cancellationToken = default)
    {
        var model = await BuildModelAsync(after);
        if (before is null || before.Version is null)
        {
            _draftSets.StageNew(draftId, new DraftContent
            {
                Key = model.Key,
                ContentTypeKey = model.ContentTypeKey,
                ParentKey = model.ParentKey,
                Names = model.Variants.Where(v => v.Segment is null).ToDictionary(v => v.Culture ?? string.Empty, v => v.Name),
                Values = model.Values,
            });
            return;
        }

        // What the edit changed, as the property values (or names) it touched: a change anywhere inside a block
        // property is a change to that property's value in its own variant.
        var type = DocumentType(after.Subtype);
        var map = await _mapper.MapAsync(type, isDocument: true);
        var touched = new HashSet<(string Alias, string? Culture, string? Segment)>();
        foreach (var change in EntityDiff.Compare(before.Fields, after.Fields, map.Schema, await _itemTypes.ResolveAsync(map.Schema, null)))
        {
            var alias = change.Path.Split('/')[0];
            if (alias == CmsSchemaMapper.NameField)
            {
                touched.Add((DraftSetService.NameAlias, type.VariesByCulture() ? change.Variant.Culture : null, null));
            }
            else if (map.Properties.TryGetValue(alias, out var property))
            {
                var variant = change.Variant.Narrow(property.Field.VariesBy);
                touched.Add((alias, variant.Culture, variant.Segment));
            }
        }

        var values = touched.Select(t => t.Alias == DraftSetService.NameAlias
            ? new DraftValue(t.Alias, t.Culture, null, model.Variants.FirstOrDefault(v => v.Culture == t.Culture && v.Segment is null)?.Name is { } name ? JsonValue.Create(name) : null)
            : model.Values.FirstOrDefault(v => v.Alias == t.Alias && v.Culture == t.Culture && v.Segment == t.Segment) ?? new DraftValue(t.Alias, t.Culture, t.Segment, null));
        await _draftSets.StageChangesAsync(draftId, EntityUdis.For(Type, after.Key), values);
    }

    public async Task<IReadOnlyList<RebasedEntity>> GetRebasedAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        var result = new List<RebasedEntity>();
        foreach (var rebase in await _draftSets.GetRebaseAsync(draftId))
        {
            var live = await LoadAsync(rebase.Entity.Guid, cancellationToken);
            var entity = live is not null ? ToSummary(live) : new EntitySummary(Type, rebase.Entity.Guid, "(deleted)", EditUrl(rebase.Entity.Guid));
            if (rebase.DeletedLive)
            {
                result.Add(new RebasedEntity(entity, ["(deleted)"], [new ChangeSetConflict(entity, string.Empty, null, null, null, null, null)]));
                continue;
            }

            result.Add(new RebasedEntity(
                entity,
                rebase.ChangedSince.Select(c => c.Culture is null ? PathOf(c.Alias) : $"{PathOf(c.Alias)} ({c.Culture})").Distinct().ToList(),
                rebase.Conflicts.Select(c => new ChangeSetConflict(entity, PathOf(c.Alias), CmsVariants.Of(c.Culture, c.Segment).ToJson(), c.Base, c.Draft, c.Live, null)).ToList()));
        }

        return result;
    }

    public Task ResolveConflictAsync(Guid draftId, Guid key, string path, VariantId variant, ConflictSide keep, CancellationToken cancellationToken = default)
        => _draftSets.ResolveAsync(
            draftId,
            EntityUdis.For(Type, key),
            path == CmsSchemaMapper.NameField ? DraftSetService.NameAlias : path,
            variant.Culture,
            variant.Segment,
            keep == ConflictSide.Yours ? DraftKeep.Draft : DraftKeep.Live);

    public Task ApplyDraftAsync(Guid draftId, CancellationToken cancellationToken = default) => _draftSets.ApplyAsync(draftId, _actingUser());

    public Task DiscardDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        _draftSets.Discard(draftId);
        return Task.CompletedTask;
    }

    private static string PathOf(string alias) => alias == DraftSetService.NameAlias ? CmsSchemaMapper.NameField : alias;

    private static string EditUrl(Guid key) => $"/umbraco/section/content/workspace/document/edit/{key}";

    private IContentType DocumentType(string? alias)
        => _contentTypes.Get(alias ?? string.Empty) is { IsElement: false } type
            ? type
            : throw new AbilityInputException($"No document type '{alias}'.", [new Problem("subtype", "Not a document type.")]);

    private JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        _ => JsonNode.Parse(_json.Serialize(value)),
    };

    // ── Reading stored values into fields ──────────────────────────────────────────────────

    /// <summary>
    /// Property values (a document's or an element's) into an object, each by its editor's handler: a field's value, or
    /// the items of an items field.
    /// </summary>
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

    /// <summary>An element's values (a block's, or its settings) into an object, for a handler whose values hold elements.</summary>
    private async Task ReadElementAsync(JsonObject into, Guid elementTypeKey, IEnumerable<BlockPropertyValue> values)
        => await ReadValuesAsync(into, values.Select(v => (v.Alias, v.Culture, v.Segment, ToNode(v.Value))), await _mapper.MapAsync(_mapper.RequireType(elementTypeKey), isDocument: false));

    // ── Writing the working copy as a content model ────────────────────────────────────────────

    private async Task<ContentModel> BuildModelAsync(Entity document)
    {
        var type = DocumentType(document.Subtype);
        var map = await _mapper.MapAsync(type, isDocument: true);
        var root = document.Fields;
        var cultures = EntityValues.VariantsIn(root, map.Schema, await _itemTypes.ResolveAsync(map.Schema, null)).Select(v => v.Culture).OfType<string>().Distinct().ToList();
        var nameField = map.Schema.Find(CmsSchemaMapper.NameField)!;
        var index = new ErrorIndex();

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
        // document values in that segment. So name every segment the document's own values are in, as the backoffice does.
        if (type.VariesBySegment())
        {
            // A value that varies by segment only has no culture, but a culture-variant document's variants all need one
            // (ContentEditingServiceBase refuses a variant without): name its segment in each of the document's cultures.
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

        var values = new List<DraftValue>();
        foreach (var property in map.Properties.Values)
        {
            foreach (var (variant, value) in Values(root, property))
            {
                var node = await ToNodeAsync(value, property, variant, cultures, variant.Culture, property.Alias, index.Properties);
                values.Add(new DraftValue(property.Alias, variant.Culture, variant.Segment, node));
            }
        }

        var properties = values.Select(v => new PropertyValueModel { Alias = v.Alias, Value = ToModelValue(v.Value), Culture = v.Culture, Segment = v.Segment }).ToList();
        var parent = root[CmsSchemaMapper.ParentField] is { } parentNode && Guid.TryParse(parentNode.ToString(), out var parentKey) ? parentKey : (Guid?)null;
        return new ContentModel(document.Key, type.Key, parent, variants, properties, values, index);
    }

    /// <summary>A property's values in the working copy: once if invariant, else once per variant that has one.</summary>
    private static IEnumerable<(VariantId Variant, JsonNode Value)> Values(JsonObject owner, PropertyMap property)
        => EntityValues.Values(owner, property.Field).Where(v => property.Field.IsItems || v.Value is not JsonArray { Count: 0 });

    /// <summary>A field's value in editor format, by the property's editor handler, which also says how to locate errors in it.</summary>
    private async Task<JsonNode?> ToNodeAsync(JsonNode value, PropertyMap property, VariantId variant, IReadOnlyList<string> documentCultures, string? culture, string path, PropertyErrorLocators errors)
    {
        var writer = new PropertyValueWriter(property.Context, property.Field, path, culture, documentCultures,
            locator => errors.Add(property.Alias, variant, locator),
            (from, elementTypeKey, elementPath, elementCulture) => WriteElementAsync(from, elementTypeKey, elementPath, elementCulture, documentCultures));
        return await property.Handler.WriteAsync(value, writer);
    }

    /// <summary>An element's values (a block's, or its settings) in editor format, for a handler whose values hold elements.</summary>
    private async Task<ElementValues> WriteElementAsync(JsonObject from, Guid elementTypeKey, string path, string? culture, IReadOnlyList<string> documentCultures)
    {
        var map = await _mapper.MapAsync(_mapper.RequireType(elementTypeKey), isDocument: false);
        var errors = new PropertyErrorLocators();
        var values = new List<BlockPropertyValue>();
        foreach (var property in map.Properties.Values)
        {
            foreach (var (variant, value) in Values(from, property))
            {
                values.Add(new BlockPropertyValue
                {
                    Alias = property.Alias,
                    Culture = variant.Culture,
                    Segment = variant.Segment,
                    Value = ToModelValue(await ToNodeAsync(value, property, variant, documentCultures, variant.Culture ?? culture, $"{path}/{property.Alias}", errors)),
                });
            }
        }

        var variesByCulture = map.Variations.VariesByCulture();
        var cultures = variesByCulture ? EntityValues.OwnVariants(from, map.Schema).Select(v => v.Culture).OfType<string>().Distinct().ToList() : [];
        return new ElementValues(values, errors, variesByCulture, cultures);
    }

    private static object? ToModelValue(JsonNode? node)
        => node is null ? null : JsonSerializer.Deserialize<object?>(node.ToJsonString(), ModelValueOptions);

    private sealed record ContentModel(Guid Key, Guid ContentTypeKey, Guid? ParentKey, List<VariantModel> Variants, List<PropertyValueModel> Properties, List<DraftValue> Values, ErrorIndex Index)
    {
        public ContentCreateModel ToCreateModel() => new()
        {
            Key = Key,
            ContentTypeKey = ContentTypeKey,
            ParentKey = ParentKey,
            Variants = Variants,
            Properties = Properties,
        };

        // Only the cultures the document has: validating all of them fails mandatory values in cultures it doesn't exist in.
        public ValidateContentUpdateModel ToValidateUpdateModel() => new()
        {
            Variants = Variants,
            Properties = Properties,
            Cultures = Variants.Any(v => v.Culture is not null) ? Variants.Select(v => v.Culture!).Where(c => c is not null).ToHashSet() : null,
        };
    }

    /// <summary>
    /// Turns Umbraco's validation errors (alias, culture, segment and a JSON path into the stored value) back into the
    /// paths the entity abilities use. A JSON path is located by the handler that wrote the value, if it said how.
    /// </summary>
    private sealed class ErrorIndex
    {
        public PropertyErrorLocators Properties { get; } = new();

        public Problem ToProblem(PropertyValidationError error)
        {
            // Same fields as Umbraco's error; only the alias and JSON path become one entity path.
            var located = new Problem(error.Alias, error.ErrorMessages.ToList()).In(CmsVariants.Of(error.Culture, error.Segment));
            if (string.IsNullOrEmpty(error.JsonPath) || error.JsonPath == "$")
            {
                return located;
            }

            return Properties.Locate(error.Alias, error.Culture, error.Segment, error.JsonPath) is { } at
                ? located with { Path = at.Path, Variant = CmsVariants.Of(at.Culture, at.Segment).ToJson() }
                : located with { Path = $"{error.Alias} {error.JsonPath}" };
        }
    }
}

/// <summary>Content's step of applying a change set runs in one Umbraco scope, so every document saves or none does.</summary>
public sealed class CmsScopeUnitOfWorkProvider(ICoreScopeProvider scopes) : IUnitOfWorkProvider
{
    public string Name => "Content";

    // Umbraco scopes are synchronous; the interface is asynchronous for products whose transactions aren't.
    public Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) => Task.FromResult<IUnitOfWork>(new Scope(scopes.CreateCoreScope()));

    private sealed class Scope(ICoreScope scope) : IUnitOfWork
    {
        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            scope.Complete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            scope.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
