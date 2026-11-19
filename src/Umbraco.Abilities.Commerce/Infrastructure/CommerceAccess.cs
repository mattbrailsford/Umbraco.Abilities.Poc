using Umbraco.Cms.Core.Services;
using Umbraco.Commerce.Core.Models;

namespace Umbraco.Abilities.Commerce;

/// <summary>Who may use a store: users Commerce allows, by user or user group, who also have the Commerce section.</summary>
public sealed class CommerceAccess(IUserService users)
{
    public const string Section = "commerce";

    public async Task<string?> CheckAsync(Guid userKey, StoreReadOnly? store)
    {
        if (await users.GetAsync(userKey) is not { } user)
        {
            return "Unknown user.";
        }

        if (!user.AllowedSections.Contains(Section))
        {
            return "You don't have the Commerce section.";
        }

        return store is null || store.IsAllowed(user.Key.ToString(), user.Groups.Select(g => g.Key.ToString()))
            ? null
            : $"You don't have access to the store '{store.Name}'.";
    }
}
