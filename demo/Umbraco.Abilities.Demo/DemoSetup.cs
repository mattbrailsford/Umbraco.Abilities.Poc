using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Umbraco.Abilities.Demo;

/// <summary>
/// Gives a new demo site something to work with, once it is installed: an article document type, and an API user the
/// MCP proxy signs in as. Both are made only if missing, so it is safe on every start. The API user's secret comes from
/// configuration (<c>Demo:Mcp:ClientSecret</c>), never from a file in the repo.
/// </summary>
public sealed class DemoSetup(
    IRuntimeState runtime,
    IContentTypeService contentTypes,
    IShortStringHelper shortStrings,
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

        await EnsureArticleTypeAsync();
        await EnsureMcpUserAsync();
    }

    private async Task EnsureArticleTypeAsync()
    {
        if (contentTypes.Get("article") is not null)
        {
            return;
        }

        var article = new ContentType(shortStrings, -1)
        {
            Alias = "article",
            Name = "Article",
            Description = "A news article: a title and a short summary.",
            Icon = "icon-newspaper",
            AllowedAsRoot = true,
        };
        article.AddPropertyType(new PropertyType(shortStrings, Constants.PropertyEditors.Aliases.TextBox, ValueStorageType.Nvarchar, "title")
        {
            Name = "Title",
            Description = "The headline.",
            Mandatory = true,
            DataTypeId = Constants.DataTypes.Textbox,
        });
        article.AddPropertyType(new PropertyType(shortStrings, Constants.PropertyEditors.Aliases.TextArea, ValueStorageType.Ntext, "summary")
        {
            Name = "Summary",
            Description = "One or two sentences for lists and search results.",
            DataTypeId = Constants.DataTypes.Textarea,
        });

        await contentTypes.CreateAsync(article, Constants.Security.SuperUserKey);
        logger.LogInformation("Demo: created the article document type.");
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
