using System.Linq.Expressions;

using Microsoft.Extensions.DependencyInjection;

using NUnit.Framework;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.ManagementApi;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Tests.Common.Builders;
using Umbraco.Cms.Tests.Common.Builders.Extensions;
using Umbraco.Cms.Tests.Integration.ManagementApi;

namespace Umbraco.Abilities.Cms.Tests;

/// <summary>
/// A real Umbraco on SQLite, served by Umbraco's test server, with the abilities and their Management API endpoints,
/// and an article document type: a name and a mandatory title.
/// </summary>
public abstract class AbilitiesApiTestBase : ManagementApiTest<AbilitiesController>
{
    protected const string Password = "1234567890";

    protected override Expression<Func<AbilitiesController, object>> MethodSelector { get; set; } = x => x.List(CancellationToken.None);

    protected IContentService ContentService => GetRequiredService<IContentService>();

    /// <summary>A consumer that builds forms for a person in the backoffice, such as a change set screen.</summary>
    protected static readonly IAbilityConsumer FormConsumer = new AbilityConsumer(
        "test-forms", "Test forms", "Forms a person fills in, in the backoffice.", AbilityAudiences.Person,
        ConsumerCapabilities.Conversation | ConsumerCapabilities.AsksConsent, [FrameworkFilters.ForForms]);

    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        builder.AddAbilities().AddAbilitiesManagementApi();
        builder.Services.AddSingleton(FormConsumer);
    }

    // The test server only serves the CMS's own controllers unless told about others.
    protected override void CustomMvcSetup(IMvcBuilder mvcBuilder)
        => mvcBuilder.AddApplicationPart(typeof(AbilitiesController).Assembly);

    [SetUp]
    public async Task CreateArticleTypeAsync()
    {
        var article = new ContentTypeBuilder()
            .WithAlias("article").WithName("Article").WithContentVariation(ContentVariation.Nothing)
            .AddPropertyType()
                .WithAlias("title").WithName("Title")
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(ContentVariation.Nothing).WithMandatory(true)
                .Done()
            .Build();
        article.AllowedAsRoot = true;
        await GetRequiredService<IContentTypeService>().CreateAsync(article, Constants.Security.SuperUserKey);
    }

    // A new user each test: the base class caches sign-ins by user name, and each test here has a new database.
    protected async Task<string> SignInAsEditorAsync()
    {
        var email = $"editor-{Guid.NewGuid():N}@umbraco.com";
        await AuthenticateClientAsync(Client, email, Password, isAdmin: false);
        return email;
    }
}
