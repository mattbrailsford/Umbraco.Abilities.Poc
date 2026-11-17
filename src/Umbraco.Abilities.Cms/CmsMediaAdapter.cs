using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace Umbraco.Abilities.Cms;

/// <summary>
/// The entity adapter for CMS media: entity type <c>media</c>, whose subtypes are the media types. Built on
/// <see cref="ContentTypeEntityAdapterBase{TType}"/>, so it is only what is about media: loading and saving through
/// <see cref="IMediaEditingService"/>, and the Media section's permissions. Media has no drafts, so a change set keeps
/// and replays its edits, and saves them when it is applied.
/// </summary>
public sealed class CmsMediaAdapter(
    CmsContentSchemas schemas,
    IMediaTypeService mediaTypes,
    IMediaEditingService editing,
    IEntityService entities,
    IUserService users,
    IMediaPermissionService permissions,
    IUnitOfWorkProvider unitOfWork,
    Func<Guid> actingUser)
    : ContentTypeEntityAdapterBase<IMediaType>(schemas)
{
    public const string Type = Constants.UdiEntityType.Media;

    public override string EntityType => Type;

    public override string DisplayName => "Media";

    public override IUnitOfWorkProvider UnitOfWork { get; } = unitOfWork;

    protected override IEnumerable<IMediaType> ListTypes() => mediaTypes.GetAll();

    protected override IMediaType? FindType(string alias) => mediaTypes.Get(alias);

    protected override IMediaType? FindType(Guid key) => mediaTypes.Get(key);

    protected override string EditUrl(Guid key) => $"/umbraco/section/media/workspace/media/edit/{key}";

    protected override async Task<StoredContent?> LoadStoredAsync(Guid key, CancellationToken cancellationToken)
        => await editing.GetAsync(key) is { } media ? ReadStored(media) : null;

    public override async Task<IReadOnlyList<Problem>> ValidateAsync(Entity media, CancellationToken cancellationToken = default)
    {
        var content = await ToContentAsync(media, cancellationToken);
        var attempt = media.Version is null
            ? await editing.ValidateCreateAsync(CreateModel(content))
            : await editing.ValidateUpdateAsync(media.Key, UpdateModel(content));

        var problems = attempt.Result.ValidationErrors.Select(content.ToProblem).ToList();
        if (!attempt.Success && attempt.Status != ContentEditingOperationStatus.PropertyValidationError)
        {
            problems.Add(new Problem(string.Empty, $"Umbraco refused the media: {attempt.Status}."));
        }

        return problems;
    }

    public override async Task<EntitySummary> SaveAsync(Entity media, CancellationToken cancellationToken = default)
    {
        var content = await ToContentAsync(media, cancellationToken);
        var status = media.Version is null
            ? (await editing.CreateAsync(CreateModel(content), actingUser())).Status
            : (await editing.UpdateAsync(media.Key, UpdateModel(content), actingUser())).Status;
        return status == ContentEditingOperationStatus.Success
            ? ToSummary(media)
            : throw new InvalidOperationException($"Umbraco refused the media: {status}.");
    }

    public override Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySummary>>(entities.GetAll(UmbracoObjectTypes.Media)
            .Where(e => e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new EntitySummary(Type, e.Key, e.Name!, EditUrl(e.Key)))
            .ToList());

    /// <summary>The backoffice's checks: the Media section, and access to the item (or, for a new one, its parent or the root).</summary>
    public override async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
    {
        var user = await users.GetAsync(request.UserKey);
        if (user is null)
        {
            return "Unknown user.";
        }

        if (!user.AllowedSections.Contains(Constants.Applications.Media))
        {
            return "You don't have access to the Media section.";
        }

        MediaAuthorizationStatus status;
        if (request.Permission == EntityPermission.Create)
        {
            var parent = request.Entity?.Fields[ContentFields.Parent] is { } node && Guid.TryParse(node.ToString(), out var key) ? key : (Guid?)null;
            status = parent is { } parentKey
                ? await permissions.AuthorizeAccessAsync(user, parentKey)
                : await permissions.AuthorizeRootAccessAsync(user);
        }
        else
        {
            status = await permissions.AuthorizeAccessAsync(user, request.Key);
        }

        return status switch
        {
            MediaAuthorizationStatus.Success => null,
            MediaAuthorizationStatus.NotFound => "Not found.",
            _ => "You don't have access to this media.",
        };
    }

    private static MediaCreateModel CreateModel(ContentValues content) => new()
    {
        Key = content.Key,
        ContentTypeKey = content.TypeKey,
        ParentKey = content.ParentKey,
        Variants = content.Variants,
        Properties = content.Properties,
    };

    private static MediaUpdateModel UpdateModel(ContentValues content) => new()
    {
        Variants = content.Variants,
        Properties = content.Properties,
    };
}
