using System.Linq.Expressions;

using Microsoft.Extensions.DependencyInjection;

using NUnit.Framework;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities;
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
    protected sealed class FormConsumer : IAbilityConsumer
    {
        public const string ConsumerAlias = "test-forms";

        public string Alias => ConsumerAlias;

        public string Name => "Test forms";

        public string Description => "Forms a person fills in, in the backoffice.";

        public string Audience => AbilityAudiences.Person;

        public IReadOnlyCollection<string> Capabilities { get; } = [ConsumerCapabilities.Conversation, ConsumerCapabilities.AsksConsent];

        public IReadOnlyList<string> Filters { get; } = [FrameworkFilters.ForForms];
    }

    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        builder.AddAbilities().AddAbilitiesManagementApi();
        builder.AbilityConsumers().Add<FormConsumer>();

        // A package's own ability, added the way a package would.
        builder.Abilities().Add<CatalogueAbility>();
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

    /// <summary>
    /// A package's own ability. It asks for the registries the site builds, the abilities among them, so it shows a
    /// package's ability can need a service that needs it.
    /// </summary>
    [Ability("test.catalogue", "Catalogue", Kind = AbilityKind.Read, Description = "Lists the entity types and abilities on this site.")]
    protected sealed class CatalogueAbility(EntityAdapterRegistry adapters, AbilityRegistry abilities)
        : AbilityBase<Catalogue>
    {
        protected override Task<AbilityResult> ExecuteAsync(AbilityContext context, CancellationToken cancellationToken)
            => Task.FromResult(Success(new Catalogue(
                adapters.All.Select(a => a.EntityType).Order().ToList(),
                abilities.All.Select(a => a.Descriptor.Id).Order().ToList())));
    }

    /// <summary>What <see cref="CatalogueAbility"/> lists.</summary>
    protected sealed record Catalogue(IReadOnlyList<string> EntityTypes, IReadOnlyList<string> Abilities);
}
