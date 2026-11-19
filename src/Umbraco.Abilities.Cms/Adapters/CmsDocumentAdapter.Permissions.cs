using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services.AuthorizationStatus;

namespace Umbraco.Abilities.Cms;

// Who may change a document: the backoffice's own checks, through the CMS's services.
public sealed partial class CmsDocumentAdapter
{
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
}
