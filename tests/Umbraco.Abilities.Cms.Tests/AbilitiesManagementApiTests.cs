using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using NUnit.Framework;

using Umbraco.Abilities.AI;
using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.ManagementApi;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Tests.Common.Builders;
using Umbraco.Cms.Tests.Common.Builders.Extensions;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.ManagementApi;

namespace Umbraco.Abilities.Cms.Tests;

/// <summary>
/// Abilities over the Management API, called the way the Umbraco CMS MCP Dev server would: list the abilities to build
/// tools, then call them over HTTP as a signed-in user, naming the change set in the <c>Umb-Change-Set</c> header.
/// A real Umbraco on SQLite, served by Umbraco's test server.
/// </summary>
[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, Boot = true)]
public sealed class AbilitiesManagementApiTests : ManagementApiTest<AbilitiesController>
{
    private const string Password = "1234567890";

    protected override Expression<Func<AbilitiesController, object>> MethodSelector { get; set; } = x => x.List(CancellationToken.None);

    private IContentService ContentService => GetRequiredService<IContentService>();

    protected override void CustomTestSetup(IUmbracoBuilder builder) => builder.AddAbilities();

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

    [Test]
    public async Task The_Management_API_lists_every_ability_with_what_a_tool_needs()
    {
        var editor = await SignInAsEditorAsync();

        var response = await Client.GetAsync(Url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());

        var abilities = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.That(abilities.Keys, Is.SupersetOf(new[] { "entity.create", "entity.setValue", "changeSet.create", "changeSet.apply" }));

        var create = abilities["entity.create"];
        Assert.Multiple(() =>
        {
            Assert.That(create.GetProperty("toolName").GetString(), Is.EqualTo("entity_create"));
            Assert.That(create.GetProperty("kind").GetString(), Is.EqualTo("Write"));
            Assert.That(create.GetProperty("usesChangeSet").GetBoolean(), Is.True);
            Assert.That(create.GetProperty("description").GetString(), Is.Not.Empty);

            // The input schema is the tool's, change set id included, so a caller passes it straight to its tool.
            var properties = create.GetProperty("inputSchema").GetProperty("properties");
            Assert.That(properties.TryGetProperty("entityType", out _), Is.True);
            Assert.That(properties.TryGetProperty(AbilityAIFunction.ChangeSetIdArgument, out _), Is.True);

            // Starting a set works outside one, so it takes no change set id.
            Assert.That(abilities["changeSet.create"].GetProperty("usesChangeSet").GetBoolean(), Is.False);
            Assert.That(abilities["changeSet.create"].GetProperty("inputSchema").GetProperty("properties").TryGetProperty(AbilityAIFunction.ChangeSetIdArgument, out _), Is.False);
        });
    }

    [Test]
    public async Task A_caller_builds_a_page_in_a_change_set_named_in_a_header_and_it_is_saved_as_that_user_on_apply()
    {
        var editor = await SignInAsEditorAsync();

        var set = (await OkAsync("changeSet.create", """{ "name": "Hello page" }""")).GetProperty("changeSetId").GetGuid();
        var page = (await OkAsync("entity.create", """{ "entityType": "document", "subtype": "article", "fields": { "name": "Hello" } }""", set))
            .GetProperty("key").GetGuid();
        await OkAsync("entity.setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "/title", "value": "Hello world" }""", set);

        var validation = await OkAsync("changeSet.validate", "{}", set);
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.True, validation.ToString());
        Assert.That(ContentService.GetById(page), Is.Null, "nothing is saved before apply");

        await OkAsync("changeSet.apply", "{}", set);

        var content = ContentService.GetById(page)!;
        var user = GetRequiredService<IUserService>().GetByEmail(editor)!;
        Assert.Multiple(() =>
        {
            Assert.That(content.Name, Is.EqualTo("Hello"));
            Assert.That(content.GetValue<string>("title"), Is.EqualTo("Hello world"));
            Assert.That(content.WriterId, Is.EqualTo(user.Id), "saved as the signed-in user, not as a system user");
        });
    }

    [Test]
    public async Task A_mistake_the_caller_can_fix_is_a_400_with_problems_by_path()
    {
        var editor = await SignInAsEditorAsync();
        var set = (await OkAsync("changeSet.create", """{ "name": "Mistakes" }""")).GetProperty("changeSetId").GetGuid();

        var (status, body) = await InvokeAsync("entity.create", """{ "entityType": "document", "subtype": "noSuchType", "fields": { "name": "Hello" } }""", set);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest), body.ToString());
            Assert.That(body.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("correctable").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("changeSet").GetProperty("id").GetGuid(), Is.EqualTo(set));
        });
    }

    [Test]
    public async Task A_user_without_the_Content_section_is_refused_with_a_403()
    {
        var mediaOnly = new UserGroupBuilder()
            .WithAlias("mediaOnly").WithName("Media only").WithAllowedSections([Constants.Applications.Media])
            .Build();
        var group = (await GetRequiredService<IUserGroupService>().CreateAsync(mediaOnly, Constants.Security.SuperUserKey)).Result;
        await AuthenticateClientAsync(Client, $"media-{Guid.NewGuid():N}@umbraco.com", Password, group.Key);

        var set = (await OkAsync("changeSet.create", """{ "name": "Not allowed" }""")).GetProperty("changeSetId").GetGuid();
        var (status, body) = await InvokeAsync("entity.create", """{ "entityType": "document", "subtype": "article", "fields": { "name": "Hello" } }""", set);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.Forbidden), body.ToString());
            Assert.That(body.GetProperty("message").GetString(), Does.Contain("Content section"));
        });
    }

    [Test]
    public async Task A_header_naming_an_unknown_change_set_is_refused_rather_than_writing_live()
    {
        var editor = await SignInAsEditorAsync();

        var unknown = Guid.NewGuid();
        var (status, body) = await InvokeAsync("entity.create", """{ "entityType": "document", "subtype": "article", "fields": { "name": "Hello" } }""", unknown);

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest), body.ToString());
        Assert.That(body.GetProperty("message").GetString(), Does.Contain(unknown.ToString()), "refused because of the set it named");
        Assert.That(ContentService.GetRootContent(), Is.Empty);
    }

    [Test]
    public async Task A_caller_that_is_not_signed_in_is_refused()
    {
        var response = await Client.GetAsync(Url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // A new user each test: the base class caches sign-ins by user name, and each test here has a new database.
    private async Task<string> SignInAsEditorAsync()
    {
        var email = $"editor-{Guid.NewGuid():N}@umbraco.com";
        await AuthenticateClientAsync(Client, email, Password, isAdmin: false);
        return email;
    }

    private async Task<JsonElement> OkAsync(string ability, string json, Guid? changeSet = null)
    {
        var (status, body) = await InvokeAsync(ability, json, changeSet);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), $"{ability} failed: {body}");
        return body.GetProperty("result");
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> InvokeAsync(string ability, string json, Guid? changeSet = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GetManagementApiUrl<AbilitiesController>(x => x.Invoke(ability, null, CancellationToken.None)))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (changeSet is { } id)
        {
            request.Headers.Add(ChangeSetAccessor.HeaderName, id.ToString());
        }

        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
