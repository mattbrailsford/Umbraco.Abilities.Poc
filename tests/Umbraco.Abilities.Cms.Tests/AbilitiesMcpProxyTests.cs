using System.IO.Pipelines;
using System.Text.Json;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using NUnit.Framework;

using Umbraco.Abilities.McpProxy;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Tests.Common.Testing;

namespace Umbraco.Abilities.Cms.Tests;

/// <summary>
/// The route the Umbraco CMS MCP Dev server would take, end to end: an MCP client lists tools and calls them, the
/// proxy (a stand-in for MCP Dev) forwards each call to the Management API, and a real Umbraco saves the result.
/// The client and the proxy talk MCP over in-memory pipes; the proxy talks HTTP to Umbraco's test server.
/// </summary>
[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, Boot = true)]
public sealed class AbilitiesMcpProxyTests : AbilitiesApiTestBase
{
    private McpServer? _server;
    private McpClient? _client;
    private AbilitiesMcpProxy _proxy = null!;
    private readonly List<string> _asked = [];

    // A person at the MCP client: they see each question and give the same answer.
    private Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Person(string action)
        => (question, _) =>
        {
            _asked.Add(question?.Message ?? string.Empty);
            return ValueTask.FromResult(new ElicitResult { Action = action });
        };

    [TearDown]
    public async Task StopMcpAsync()
    {
        _asked.Clear();
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Test]
    public async Task An_MCP_client_gets_one_tool_per_ability_the_site_lists()
    {
        await SignInAsEditorAsync();
        var client = await ConnectAsync();

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);

        Assert.That(tools.Keys, Is.SupersetOf(new[] { "entity_create", "entity_setValue", "changeSet_create", "changeSet_apply" }));
        var create = tools["entity_create"].ProtocolTool;
        Assert.That(create.Description, Is.Not.Empty);
        Assert.That(create.InputSchema.GetProperty("properties").TryGetProperty("changeSetId", out _), Is.True);
    }

    [Test]
    public async Task An_MCP_client_gets_schemas_without_the_hints_meant_for_UIs()
    {
        // The Management API keeps x-editor and x-meta for UIs (see its tests). A model reads none of it.
        await SignInAsEditorAsync();
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("entity_getSchema", new Dictionary<string, object?> { ["entityType"] = "document", ["subtype"] = "article" });
        var text = TextOf(result);
        Assert.That(text, Does.Contain("\"title\""));
        Assert.That(text, Does.Not.Contain("x-editor").And.Not.Contain("x-meta"));
        var tools = await client.ListToolsAsync();
        Assert.That(tools.Select(t => t.ProtocolTool.InputSchema.GetRawText()), Has.None.Contains("x-editor").And.None.Contains("x-meta"));
    }

    [Test]
    public async Task An_MCP_client_is_told_which_tools_only_read_and_which_need_a_person_to_approve_them()
    {
        await SignInAsEditorAsync();
        var client = await ConnectAsync();

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool.Annotations);

        Assert.Multiple(() =>
        {
            Assert.That(tools["entity_get"]?.ReadOnlyHint, Is.True, "a read");
            Assert.That(tools["entity_setValue"]?.ReadOnlyHint, Is.False, "a write");
            Assert.That(tools["entity_setValue"]?.DestructiveHint, Is.False, "a change set can undo it");
            Assert.That(tools["changeSet_apply"]?.DestructiveHint, Is.True, "it can't be undone, so a person approves it");
        });
    }

    [Test]
    public async Task An_MCP_client_builds_a_page_in_a_change_set_without_naming_it_and_it_is_saved_on_apply()
    {
        var editor = await SignInAsEditorAsync();
        var client = await ConnectAsync();

        // The session remembers the set it created, so later calls don't have to name it.
        await OkAsync(client, "changeSet_create", new() { ["name"] = "Hello page" });
        Assert.That(_proxy.OpenChangeSetId, Is.Not.Null);
        var page = (await OkAsync(client, "entity_create", new()
        {
            ["entityType"] = "document",
            ["subtype"] = "article",
            ["fields"] = new Dictionary<string, object?> { ["name"] = "Hello" },
        })).GetProperty("key").GetGuid();
        await OkAsync(client, "entity_setValue", new() { ["entityType"] = "document", ["key"] = page, ["path"] = "/title", ["value"] = "Hello world" });

        var validation = await OkAsync(client, "changeSet_validate", []);
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.True, validation.ToString());
        Assert.That(ContentService.GetById(page), Is.Null, "nothing is saved before apply");

        await OkAsync(client, "changeSet_apply", []);

        var content = ContentService.GetById(page)!;
        Assert.Multiple(() =>
        {
            Assert.That(_asked, Has.Count.EqualTo(1), "the person is asked once, for the apply only: " + string.Join(" | ", _asked));
            Assert.That(_asked[0], Does.Contain("Apply change set \"Hello page\""));
            Assert.That(_asked[0], Does.Contain("Document \"Hello\" (created)"), "the person sees what it saves");
            Assert.That(content.GetValue<string>("title"), Is.EqualTo("Hello world"));
            Assert.That(content.WriterId, Is.EqualTo(GetRequiredService<IUserService>().GetByEmail(editor)!.Id));
            Assert.That(_proxy.OpenChangeSetId, Is.Null, "an applied set is closed in the session");
        });
    }

    [Test]
    public async Task A_person_who_says_no_stops_a_call_that_cannot_be_undone_and_nothing_is_saved()
    {
        await SignInAsEditorAsync();
        var client = await ConnectAsync(Person("decline"));
        await OkAsync(client, "changeSet_create", new() { ["name"] = "Hello page" });
        var page = (await OkAsync(client, "entity_create", new()
        {
            ["entityType"] = "document",
            ["subtype"] = "article",
            ["fields"] = new Dictionary<string, object?> { ["name"] = "Hello", ["title"] = "Hello world" },
        })).GetProperty("key").GetGuid();

        var result = await client.CallToolAsync("changeSet_apply", new Dictionary<string, object?>());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(TextOf(result), Does.Contain("didn't agree"));
            Assert.That(ContentService.GetById(page), Is.Null, "nothing is saved");
            Assert.That(_proxy.OpenChangeSetId, Is.Not.Null, "the set stays open");
        });
    }

    [Test]
    public async Task A_client_that_cannot_ask_the_person_is_refused_a_call_that_cannot_be_undone()
    {
        await SignInAsEditorAsync();
        var client = await ConnectAsync(person: null);
        await OkAsync(client, "changeSet_create", new() { ["name"] = "Hello page" });

        var result = await client.CallToolAsync("changeSet_apply", new Dictionary<string, object?>());

        Assert.That(result.IsError, Is.True);
        Assert.That(TextOf(result), Does.Contain("can't ask them"));
    }

    [Test]
    public async Task A_refused_call_is_an_MCP_tool_error_with_the_problems_to_fix()
    {
        await SignInAsEditorAsync();
        var client = await ConnectAsync();
        await OkAsync(client, "changeSet_create", new() { ["name"] = "Mistakes" });

        var result = await client.CallToolAsync("entity_create", new Dictionary<string, object?>
        {
            ["entityType"] = "document",
            ["subtype"] = "noSuchType",
            ["fields"] = new Dictionary<string, object?> { ["name"] = "Hello" },
        });

        Assert.That(result.IsError, Is.True);
        var body = JsonDocument.Parse(TextOf(result)).RootElement;
        Assert.That(body.GetProperty("correctable").GetBoolean(), Is.True, body.ToString());
    }

    // The proxy as an MCP server, and an MCP client connected to it, over in-memory pipes. The proxy calls Umbraco with
    // the test server's signed-in HTTP client, as MCP Dev calls a site as its API user.
    // By default the client has a person who says yes. Null plays a client that can't ask anyone.
    private Task<McpClient> ConnectAsync() => ConnectAsync(Person("accept"));

    private async Task<McpClient> ConnectAsync(Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? person)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        _proxy = new AbilitiesMcpProxy(Client);
        var options = _proxy.CreateOptions();
        _server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        _ = _server.RunAsync();
        var clientOptions = person is null ? null : new McpClientOptions { Handlers = new McpClientHandlers { ElicitationHandler = person } };
        _client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), clientOptions);
        return _client;
    }

    private static async Task<JsonElement> OkAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"{tool} failed: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("result").Clone();
    }

    private static string TextOf(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;
}
