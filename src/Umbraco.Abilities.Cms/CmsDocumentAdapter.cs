using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.DraftSets;

namespace Umbraco.Abilities.Cms;

/// <summary>
/// The real entity adapter for CMS documents: one entity type, <c>document</c> (the UDI entity type), whose subtypes are
/// the document types. The schema, reading and writing values through each editor's handler, and mapping Umbraco's
/// validation errors come from <see cref="ContentTypeEntityAdapterBase{TType}"/>. This class is what is only about
/// documents: loading, validating through <see cref="IContentEditingService"/>, permissions, and drafts.
/// Documents can be deleted (to the recycle bin) and moved: both are staged in the draft set and applied with it.
/// <para>
/// It is also the adapter to CMS Draft Sets: inside a change set, content changes are staged into a CMS draft set (per
/// property) instead of being kept and replayed by the change set, so the draft set is the source of truth for content,
/// and its own rebasing, review and apply (all or nothing, in one Umbraco scope) are used.
/// </para>
/// </summary>
public sealed class CmsDocumentAdapter(
    CmsContentSchemas schemas,
    IContentTypeService contentTypes,
    IContentEditingService editing,
    IEntityService entities,
    DraftSetService draftSets,
    IUserService users,
    IContentPermissionService permissions,
    IUnitOfWorkProvider unitOfWork,
    Func<Guid> actingUser)
    : ContentTypeEntityAdapterBase<IContentType>(schemas), IDraftingEntityAdapter, IDeletableEntityAdapter, ITreeEntityAdapter
{
    public const string Type = Constants.UdiEntityType.Document;

    private readonly DraftSetService _draftSets = draftSets;
    private readonly IUserService _users = users;
    private readonly IContentPermissionService _permissions = permissions;
    private readonly Func<Guid> _actingUser = actingUser;

    public override string EntityType => Type;

    public override string DisplayName => "Document";

    public override IUnitOfWorkProvider UnitOfWork { get; } = unitOfWork;

    /// <summary>Deleted documents go to the recycle bin.</summary>
    public bool CanRestore => true;

    public string ParentField => ContentFields.Parent;

    /// <summary>Not used: a document in a change set is deleted through its draft set (<see cref="StageDeleteAsync"/>).</summary>
    public Task DeleteAsync(Guid key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Content is deleted through its CMS draft set.");

    protected override IEnumerable<IContentType> ListTypes() => contentTypes.GetAll().Where(t => !t.IsElement);

    protected override IContentType? FindType(string alias) => contentTypes.Get(alias) is { IsElement: false } type ? type : null;

    protected override IContentType? FindType(Guid key) => contentTypes.Get(key);

    protected override string EditUrl(Guid key) => $"/umbraco/section/content/workspace/document/edit/{key}";

    // Live content is read through the draft set service, which reads it as the backoffice does.
    protected override async Task<StoredContent?> LoadStoredAsync(Guid key, CancellationToken cancellationToken)
        => await _draftSets.GetLiveAsync(EntityUdis.For(Type, key)) is { } live ? Stored(live) : null;

    public override async Task<IReadOnlyList<Problem>> ValidateAsync(Entity document, CancellationToken cancellationToken = default)
    {
        var content = await ToContentAsync(document, cancellationToken);
        var attempt = document.Version is null
            ? await editing.ValidateCreateAsync(
                new ContentCreateModel { Key = content.Key, ContentTypeKey = content.TypeKey, ParentKey = content.ParentKey, Variants = content.Variants, Properties = content.Properties },
                _actingUser())
            : await editing.ValidateUpdateAsync(
                document.Key,
                new ValidateContentUpdateModel { Variants = content.Variants, Properties = content.Properties, Cultures = content.Cultures },
                _actingUser());

        var problems = attempt.Result.ValidationErrors.Select(content.ToProblem).ToList();
        if (!attempt.Success && attempt.Status != ContentEditingOperationStatus.PropertyValidationError)
        {
            problems.Add(new Problem(string.Empty, $"Umbraco refused the document: {attempt.Status}."));
        }

        // Umbraco checks a move only when it runs, so check here what it would refuse: a parent that doesn't allow the
        // document's type, or one inside the document itself.
        if (document.Version is not null
            && await _draftSets.GetLiveAsync(EntityUdis.For(Type, document.Key)) is { } live
            && live.ParentKey != content.ParentKey
            && MoveProblem(document, content) is { } moveProblem)
        {
            problems.Add(moveProblem);
        }

        return problems;
    }

    private Problem? MoveProblem(Entity document, ContentValues content)
    {
        if (content.ParentKey is not { } parentKey)
        {
            return FindType(content.TypeKey) is { AllowedAsRoot: true } ? null : new Problem(ContentFields.Parent, "This document type isn't allowed at the root.");
        }

        // A parent created in the same set isn't saved yet; Umbraco checks it when the move runs.
        if (entities.Get(parentKey, UmbracoObjectTypes.Document) is not { } parent)
        {
            return null;
        }

        if (parent.Path.Split(',').Contains(entities.Get(document.Key, UmbracoObjectTypes.Document)?.Id.ToString()))
        {
            return new Problem(ContentFields.Parent, "A document can't be moved under itself.");
        }

        var parentType = contentTypes.Get(((Umbraco.Cms.Core.Models.Entities.IContentEntitySlim)parent).ContentTypeKey);
        return parentType?.AllowedContentTypes?.Any(t => t.Key == content.TypeKey) == true
            ? null
            : new Problem(ContentFields.Parent, $"{parent.Name} doesn't allow this document type under it.");
    }

    /// <summary>Not used: content in a change set is applied through its draft set (<see cref="ApplyDraftAsync"/>).</summary>
    public override Task<EntitySummary> SaveAsync(Entity document, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Content is applied through its CMS draft set.");

    public override Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySummary>>(entities.GetAll(UmbracoObjectTypes.Document)
            .Where(e => e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new EntitySummary(Type, e.Key, e.Name!, EditUrl(e.Key)))
            .ToList());

    // ── Authorization: the CMS's own permission model ─────────────────────────────────────────────

    /// <summary>
    /// The backoffice's checks, through the CMS's own services: the Content section, the document permission on the
    /// document (or, for a new one, its parent or the root), and access to the cultures the change touches.
    /// </summary>
    public override async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
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
            EntityPermission.Delete => (ActionDelete.ActionLetter, "delete"),
            EntityPermission.Move => (ActionMove.ActionLetter, "move"),
            _ => (ActionUpdate.ActionLetter, "update"),
        };

        // A new document is checked on its parent. Every other check is on the document itself.
        var parent = request.Entity?.Fields[ContentFields.Parent] is { } node && Guid.TryParse(node.ToString(), out var key) ? key : (Guid?)null;
        var status = request.Permission == EntityPermission.Create
            ? await OnParentAsync(user, parent, permission)
            : await _permissions.AuthorizeAccessAsync(user, request.Key, permission);

        // As the backoffice does: a move also needs create permission on the new parent.
        if (status == ContentAuthorizationStatus.Success && request.Permission == EntityPermission.Move)
        {
            status = await OnParentAsync(user, parent, ActionNew.ActionLetter);
            verb = "move this document there";
        }

        if (status != ContentAuthorizationStatus.Success)
        {
            return status == ContentAuthorizationStatus.NotFound ? "Not found." : $"You don't have permission to {verb}{(verb.Contains(' ') ? string.Empty : " this document")}.";
        }

        var cultures = request.Variants.Select(v => v.Culture).OfType<string>().ToHashSet();
        if (cultures.Count > 0
            && await _permissions.AuthorizeCultureAccessAsync(user, cultures) != ContentAuthorizationStatus.Success)
        {
            return $"You don't have access to edit {string.Join(", ", cultures)}.";
        }

        return null;
    }

    /// <summary>
    /// A permission on a parent, or the root. A parent that isn't live yet was created in the same set, which was checked
    /// on its own parent when it was created, so it's allowed.
    /// </summary>
    private async Task<ContentAuthorizationStatus> OnParentAsync(Umbraco.Cms.Core.Models.Membership.IUser user, Guid? parent, string permission)
    {
        if (parent is not { } parentKey)
        {
            return await _permissions.AuthorizeRootAccessAsync(user, permission);
        }

        var status = await _permissions.AuthorizeAccessAsync(user, parentKey, permission);
        return status == ContentAuthorizationStatus.NotFound && entities.Get(parentKey, UmbracoObjectTypes.Document) is null
            ? ContentAuthorizationStatus.Success
            : status;
    }

    // ── CMS Draft Sets: the change set hands content changes to a CMS draft set ─────────────────────

    public Task<Guid> CreateDraftAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_draftSets.Create(name).Id);

    public async Task<Entity?> LoadDraftAsync(Guid draftId, Guid key, CancellationToken cancellationToken = default)
        => await _draftSets.GetAsync(draftId, EntityUdis.For(Type, key)) is { } content ? await ToEntityAsync(Stored(content), cancellationToken) : null;

    /// <summary>
    /// Stages an edit. New content is staged whole. For existing content only the values the edit changed are staged,
    /// per property, culture and segment, so the draft set can rebase onto values others change meanwhile.
    /// </summary>
    public async Task StageAsync(Guid draftId, Entity? before, Entity after, CancellationToken cancellationToken = default)
    {
        var content = await ToContentAsync(after, cancellationToken);
        var values = content.Values.Select(v => new DraftValue(v.Alias, v.Culture, v.Segment, v.Value)).ToList();
        if (before is null || before.Version is null)
        {
            _draftSets.StageNew(draftId, new DraftContent
            {
                Key = content.Key,
                ContentTypeKey = content.TypeKey,
                ParentKey = content.ParentKey,
                Names = content.Variants.Where(v => v.Segment is null).ToDictionary(v => v.Culture ?? string.Empty, v => v.Name),
                Values = values,
            });
            return;
        }

        var changed = (await TouchedValuesAsync(before, after, cancellationToken)).Select(t => t.Alias == ContentFields.Name
            ? new DraftValue(DraftSetService.NameAlias, t.Culture, null, content.Variants.FirstOrDefault(v => v.Culture == t.Culture && v.Segment is null)?.Name is { } name ? JsonValue.Create(name) : null)
            : values.FirstOrDefault(v => v.Alias == t.Alias && v.Culture == t.Culture && v.Segment == t.Segment) ?? new DraftValue(t.Alias, t.Culture, t.Segment, null)).ToList();

        // A new parent is a move, staged as the draft set's parent value.
        if (!JsonNode.DeepEquals(before.Fields[ContentFields.Parent], after.Fields[ContentFields.Parent]))
        {
            changed.Add(new DraftValue(DraftSetService.ParentAlias, null, null, content.ParentKey is { } parent ? JsonValue.Create(parent.ToString()) : null));
        }

        await _draftSets.StageChangesAsync(draftId, EntityUdis.For(Type, after.Key), changed);
    }

    public Task StageDeleteAsync(Guid draftId, Entity before, CancellationToken cancellationToken = default)
        => _draftSets.StageDeleteAsync(draftId, EntityUdis.For(Type, before.Key));

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
            path switch
            {
                ContentFields.Name => DraftSetService.NameAlias,
                ContentFields.Parent => DraftSetService.ParentAlias,
                _ => path,
            },
            variant.Culture,
            variant.Segment,
            keep == ConflictSide.Yours ? DraftKeep.Draft : DraftKeep.Live);

    public Task ApplyDraftAsync(Guid draftId, CancellationToken cancellationToken = default) => _draftSets.ApplyAsync(draftId, _actingUser());

    public Task DiscardDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        _draftSets.Discard(draftId);
        return Task.CompletedTask;
    }

    private static string PathOf(string alias) => alias switch
    {
        DraftSetService.NameAlias => ContentFields.Name,
        DraftSetService.ParentAlias => ContentFields.Parent,
        _ => alias,
    };

    private static StoredContent Stored(DraftContent content)
        => new(content.Key, content.ContentTypeKey, content.Names, content.Values.Select(v => new StoredValue(v.Alias, v.Culture, v.Segment, v.Value)).ToList(), content.Version, content.ParentKey);
}

/// <summary>Content's step of applying a change set runs in one Umbraco scope, so every document saves or none does.</summary>
public sealed class CmsScopeUnitOfWorkProvider(ICoreScopeProvider scopes) : IUnitOfWorkProvider
{
    public string Name => "Content";

    public async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateCoreScope();
        await work(cancellationToken);
        scope.Complete();
    }
}
