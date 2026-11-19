using Umbraco.Cms.Core.Security;

namespace Umbraco.Abilities.Cms;

/// <summary>
/// Who CMS documents and media save as. On a site, that is the backoffice user signed in to the request that applies
/// the set. A test with no request replaces it with a fixed user.
/// </summary>
public sealed class CmsActingUser(Func<Guid> key)
{
    /// <summary>The signed-in backoffice user.</summary>
    public CmsActingUser(IBackOfficeSecurityAccessor security)
        : this(() => security.BackOfficeSecurity?.CurrentUser?.Key
            ?? throw new InvalidOperationException("No backoffice user is signed in to save as."))
    {
    }

    /// <summary>The key of the user to save as, now.</summary>
    public Guid Key => key();
}
