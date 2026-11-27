using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Services;
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
public sealed partial class CmsDocumentAdapter(
    CmsContentSchemas schemas,
    IContentTypeService contentTypes,
    IContentEditingService editing,
    IEntityService entities,
    DraftSetService draftSets,
    IUserService users,
    IContentPermissionService permissions,
    CmsScopeUnitOfWorkProvider unitOfWork,
    CmsActingUser actingUser,
    IContentBlueprintEditingService blueprints)
    : ContentTypeEntityAdapterBase<IContentType>(schemas), IDraftingEntityAdapter, IDeletableEntityAdapter, ITreeEntityAdapter, ITemplatedEntityAdapter
{
    public const string Type = Constants.UdiEntityType.Document;

    private readonly DraftSetService _draftSets = draftSets;
    private readonly IUserService _users = users;
    private readonly IContentPermissionService _permissions = permissions;
    private readonly CmsActingUser _actingUser = actingUser;

    public override string EntityType => Type;

    public override string DisplayName => "Document";

    public override IUnitOfWorkProvider UnitOfWork { get; } = unitOfWork;

    /// <summary>Deleted documents go to the recycle bin.</summary>
    public bool CanRestore => true;

    public string ParentField => ContentFields.Parent;

    /// <summary>Not used: a document in a change set is deleted through its draft set (<see cref="StageAsync"/>).</summary>
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
                _actingUser.Key)
            : await editing.ValidateUpdateAsync(
                document.Key,
                new ValidateContentUpdateModel { Variants = content.Variants, Properties = content.Properties, Cultures = content.Cultures },
                _actingUser.Key);

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
    public override Task<EntitySummary> SaveAsync(Entity? before, Entity document, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Content is applied through its CMS draft set.");

    public override Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySummary>>(entities.GetAll(UmbracoObjectTypes.Document)
            .Where(e => e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new EntitySummary(Type, e.Key, e.Name!, EditUrl(e.Key), (e as IContentEntitySlim)?.ContentTypeAlias))
            .ToList());
}
