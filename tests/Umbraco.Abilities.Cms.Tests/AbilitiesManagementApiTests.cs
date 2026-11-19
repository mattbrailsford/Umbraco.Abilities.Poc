using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using NUnit.Framework;
using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.ManagementApi;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Tests.Common.Builders;
using Umbraco.Cms.Tests.Common.Builders.Extensions;
using Umbraco.Cms.Tests.Common.Testing;

namespace Umbraco.Abilities.Cms.Tests;

/// <summary>
/// Abilities over the Management API, called the way the Umbraco CMS MCP Dev server would: list the abilities to build
/// tools, then call them over HTTP as a signed-in user, naming the change set in the <c>Umb-Change-Set</c> header.
/// A real Umbraco on SQLite, served by Umbraco's test server.
/// </summary>
[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, Boot = true)]
public sealed class AbilitiesManagementApiTests : AbilitiesApiTestBase
{
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
            Assert.That(properties.TryGetProperty(AbilityTool.ChangeSetIdArgument, out _), Is.True);

            // Starting a set works outside one, so it takes no change set id.
            Assert.That(abilities["changeSet.create"].GetProperty("usesChangeSet").GetBoolean(), Is.False);
            Assert.That(abilities["changeSet.create"].GetProperty("inputSchema").GetProperty("properties").TryGetProperty(AbilityTool.ChangeSetIdArgument, out _), Is.False);
        });
    }

    [Test]
    public async Task A_consumer_that_builds_forms_gets_each_fields_editor_and_the_MCP_server_does_not()
    {
        await SignInAsEditorAsync();

        // A UI, such as a change set screen, reads x-editor and x-meta: its consumer keeps them. MCP's strips them, in
        // results too.
        const string article = """{ "entityType": "document", "subtype": "article" }""";
        var title = (await OkAsync("entity.getSchema", article, consumer: FormConsumer.Alias)).GetProperty("fields").GetProperty("properties").GetProperty("title");
        Assert.That(title.GetProperty("x-editor").GetProperty("alias").GetString(), Is.Not.Empty);
        Assert.That(title.GetProperty("x-meta").TryGetProperty("umbraco.cms.data-type", out _), Is.True);

        var forModel = (await OkAsync("entity.getSchema", article)).GetProperty("fields").GetProperty("properties").GetProperty("title");
        Assert.That(forModel.TryGetProperty("x-editor", out _) || forModel.TryGetProperty("x-meta", out _), Is.False);
    }

    [Test]
    public async Task Only_a_model_is_told_to_move_a_page_with_entity_move()
    {
        await SignInAsEditorAsync();

        // A person moves a page in the tree, not with a tool: the hint is the model's.
        const string article = """{ "entityType": "document", "subtype": "article" }""";
        string? ParentOf(JsonElement schema) => schema.GetProperty("fields").GetProperty("properties").GetProperty("parent").GetProperty("description").GetString();

        Assert.That(ParentOf(await OkAsync("entity.getSchema", article)), Does.Contain("entity.move"));
        Assert.That(ParentOf(await OkAsync("entity.getSchema", article, consumer: FormConsumer.Alias)), Does.Not.Contain("entity.move"));
    }

    [Test]
    public async Task A_form_gets_a_tools_input_with_the_fields_of_the_page_type_chosen()
    {
        await SignInAsEditorAsync();

        async Task<(HttpStatusCode Status, string Body)> InputSchemaAsync(string tool, string chosen, string consumer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GetManagementApiUrl<AbilitiesController>(x => x.InputSchema(tool, null, CancellationToken.None)))
            {
                Content = new StringContent(chosen, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(AbilitiesController.ConsumerHeader, consumer);
            using var response = await Client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // A form consumer has one Create per entity type: the page type is chosen, and the fields follow it.
        var (status, body) = await InputSchemaAsync("entity.create:document", """{ "subtype": "article" }""", FormConsumer.Alias);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body);
        var fields = JsonDocument.Parse(body).RootElement.GetProperty("properties").GetProperty("fields");
        Assert.That(fields.GetProperty("properties").TryGetProperty("title", out _), Is.True, body);
        Assert.That(fields.GetProperty("required").EnumerateArray().Select(r => r.GetString()), Does.Contain("title"));

        // A model has one entity.create for every type, so the MCP server has no such tool.
        Assert.That((await InputSchemaAsync("entity.create:document", "{}", McpConsumer.ConsumerAlias)).Status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task The_consumer_named_in_a_header_decides_the_list_and_a_cache_keeps_one_per_consumer()
    {
        await SignInAsEditorAsync();

        async Task<(HashSet<string> Ids, string Vary)> ListAsync(string? consumer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            if (consumer is not null)
            {
                request.Headers.Add(AbilitiesController.ConsumerHeader, consumer);
            }

            using var response = await Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
            var ids = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToHashSet();
            return (ids, string.Join(",", response.Headers.Vary));
        }

        var mcp = await ListAsync(null);
        var forms = await ListAsync(FormConsumer.Alias);
        Assert.Multiple(() =>
        {
            Assert.That(mcp.Ids, Does.Contain("entity.setValue"), "no header is the MCP server's consumer");
            Assert.That(forms.Ids, Does.Not.Contain("entity.setValue"));
            Assert.That(mcp.Vary, Does.Contain(AbilitiesController.ConsumerHeader));
        });
    }

    [Test]
    public async Task A_tool_the_consumer_does_not_offer_cannot_be_called_through_it()
    {
        await SignInAsEditorAsync();

        // The form consumer leaves out building blocks such as setValue.
        var (status, _) = await InvokeAsync("entity.setValue", "{}", consumer: FormConsumer.Alias);
        Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
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

        await ApplyWithConsentAsync(set);

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
    public async Task A_call_that_cannot_be_undone_is_a_428_with_a_question_and_runs_with_that_approvals_id()
    {
        await SignInAsEditorAsync();
        var set = (await OkAsync("changeSet.create", """{ "name": "Hello page" }""")).GetProperty("changeSetId").GetGuid();
        var page = (await OkAsync("entity.create", """{ "entityType": "document", "subtype": "article", "fields": { "name": "Hello" } }""", set))
            .GetProperty("key").GetGuid();
        await OkAsync("entity.setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "/title", "value": "Hello world" }""", set);

        var (status, body) = await InvokeAsync("changeSet.apply", "{}", set);

        var approval = body.GetProperty("approval");
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo((HttpStatusCode)428), body.ToString());
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("ApprovalRequired"));
            Assert.That(approval.GetProperty("question").GetString(), Does.Contain("Document \"Hello\" (created)"), "the person sees what it saves");
            Assert.That(body.GetProperty("changeSet").GetProperty("open").GetBoolean(), Is.True, "the set is still open");
            Assert.That(ContentService.GetById(page), Is.Null, "nothing is saved");
        });

        var (madeUp, _) = await InvokeAsync("changeSet.apply", "{}", set, approval: Guid.NewGuid());
        Assert.That(madeUp, Is.EqualTo((HttpStatusCode)428), "an id nobody was asked about approves nothing");

        // The made-up id asked again, so the first request is still waiting and approves the call.
        await OkAsync("changeSet.apply", "{}", set, approval: approval.GetProperty("id").GetGuid());
        Assert.That(ContentService.GetById(page), Is.Not.Null, "saved once the person agreed");
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

    // Plays a caller that asks the person, gets a yes, and sends the call again with the approval's id.
    private async Task ApplyWithConsentAsync(Guid set)
    {
        var (status, body) = await InvokeAsync("changeSet.apply", "{}", set);
        Assert.That(status, Is.EqualTo((HttpStatusCode)428), body.ToString());
        await OkAsync("changeSet.apply", "{}", set, approval: body.GetProperty("approval").GetProperty("id").GetGuid());
    }

    private async Task<JsonElement> OkAsync(string ability, string json, Guid? changeSet = null, Guid? approval = null, string consumer = McpConsumer.ConsumerAlias)
    {
        var (status, body) = await InvokeAsync(ability, json, changeSet, approval, consumer);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), $"{ability} failed: {body}");
        return body.GetProperty("result");
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> InvokeAsync(string ability, string json, Guid? changeSet = null, Guid? approval = null, string consumer = McpConsumer.ConsumerAlias)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GetManagementApiUrl<AbilitiesController>(x => x.Invoke(ability, null, CancellationToken.None)))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (changeSet is { } id)
        {
            request.Headers.Add(ChangeSetAccessor.HeaderName, id.ToString());
        }

        if (approval is { } approvalId)
        {
            request.Headers.Add(AbilitiesController.ApprovalHeader, approvalId.ToString());
        }

        request.Headers.Add(AbilitiesController.ConsumerHeader, consumer);
        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
