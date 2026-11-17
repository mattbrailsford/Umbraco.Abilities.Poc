using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Abilities.Demo;

/// <summary>
/// Adds the API user the MCP proxy signs in as, once the site is installed. It is made only if missing, so it is safe
/// on every start. Its secret comes from configuration (<c>Demo:Mcp:ClientSecret</c>), never from a file in the repo.
/// The document types and content come from the Clean starter kit.
/// </summary>
public sealed class DemoSetup(
    IRuntimeState runtime,
    IUserService users,
    IUserGroupService userGroups,
    IBackOfficeUserClientCredentialsManager clientCredentials,
    IConfiguration configuration,
    ILogger<DemoSetup> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string McpUserEmail = "mcp@example.com";

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtime.Level != RuntimeLevel.Run)
        {
            return;
        }

        await EnsureMcpUserAsync();
    }

    private async Task EnsureMcpUserAsync()
    {
        var clientId = configuration["Demo:Mcp:ClientId"];
        var clientSecret = configuration["Demo:Mcp:ClientSecret"];
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            logger.LogInformation("Demo: no MCP API user. Set Demo__Mcp__ClientSecret to create one.");
            return;
        }

        if (users.GetByEmail(McpUserEmail) is not null)
        {
            return;
        }

        // An editor: the Content and Media sections, and the default editor permissions.
        var editors = await userGroups.GetAsync(Constants.Security.EditorGroupKey);
        var created = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
        {
            Kind = UserKind.Api,
            Email = McpUserEmail,
            UserName = McpUserEmail,
            Name = "MCP",
            UserGroupKeys = new HashSet<Guid> { editors!.Key },
        }, approveUser: true);
        if (!created.Success)
        {
            logger.LogWarning("Demo: couldn't create the MCP API user: {Status}.", created.Status);
            return;
        }

        var saved = await clientCredentials.SaveAsync(created.Result.CreatedUser!.Key, clientId, clientSecret);
        logger.LogInformation("Demo: created the MCP API user, client id {ClientId}: {Status}.", clientId, saved.Success ? "ready" : saved.Result.ToString());
    }
}
