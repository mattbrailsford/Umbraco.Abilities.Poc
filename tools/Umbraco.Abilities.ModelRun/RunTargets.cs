using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Umbraco.Abilities.AI;
using Umbraco.Abilities.Samples;
using Umbraco.Abilities.Samples.Cms;

namespace Umbraco.Abilities.ModelRun;

/// <summary>Where a run happens: the tools the model gets, and the person's check and apply after it.</summary>
internal abstract class RunTarget : IAsyncDisposable
{
    protected static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>What the run is against, for the transcript.</summary>
    public abstract string Name { get; }

    /// <summary>Every tool the site offers. The run takes out the ones a person keeps.</summary>
    public abstract IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>Validates the change set the model left open, applies it as a person would, and prints what is live.</summary>
    public abstract Task ValidateAndApplyAsync();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The in-memory samples, with the AI tools made straight from the abilities.</summary>
internal sealed class SampleTarget : RunTarget
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly AbilityToolSession _session = new(SampleAccess.Editor);

    private SampleTarget() => Tools = AbilityAITools.Create(_site.Abilities, _site.Invoker, _session).ToList();

    public override string Name => "in-memory samples";

    public override IReadOnlyList<AIFunction> Tools { get; }

    public static async Task<SampleTarget> CreateAsync()
    {
        var target = new SampleTarget();
        await SeedContentAsync(target._site);
        return target;
    }

    public override async Task ValidateAndApplyAsync()
    {
        if (_session.ChangeSetId is not { } setId)
        {
            Console.WriteLine($"The model left {_session.OpenChangeSetIds.Count} change sets open, not one.");
            return;
        }

        Console.WriteLine($"Current change set: {_site.ChangeSets.Get(setId).Name}");
        var validation = await _site.ChangeSets.ValidateAsync(setId);
        var problems = validation.SelectMany(e => e.Problems).ToList();
        Console.WriteLine(problems.Count == 0 ? "Valid." : string.Join(Environment.NewLine, problems.Select(p => $"  {p.Path}: {p}")));

        var applied = await _site.ChangeSets.ApplyAsync(setId, new AbilityContext(_session.UserKey));
        Console.WriteLine($"Applied by a person: {applied.Status} {applied.Message}");

        Console.WriteLine();
        Console.WriteLine("── Live entities after apply ──");
        foreach (var saved in applied.Result as IEnumerable<EntitySummary> ?? [])
        {
            var entity = await _site.Adapters.Get(saved.EntityType).LoadAsync(saved.Key);
            Console.WriteLine(entity?.ToJson().ToJsonString(Indented));
        }
    }

    // A small site to work on: two pages in English and Danish, and two images. Saved the way an editor's save would be.
    private static async Task SeedContentAsync(SampleSite site)
    {
        var beach = new MediaRequestModel
        {
            Id = Guid.NewGuid(),
            Name = "Beach banner",
            AltText = "A sandy beach at sunset",
            Crops = [new MediaCropRequestModel { Id = Guid.NewGuid(), Alias = "wide", Width = 1920, Height = 600 }],
        };
        var team = new MediaRequestModel { Id = Guid.NewGuid(), Name = "Team photo", AltText = "The team in the office" };
        await site.Media.SaveModelAsync(beach);
        await site.Media.SaveModelAsync(team);

        await site.Pages.SaveModelAsync(new Page
        {
            Key = Guid.NewGuid(),
            Title = { ["en-US"] = "Summer sale", ["da-DK"] = "Sommerudsalg" },
            UrlSegment = { ["en-US"] = "summer-sale", ["da-DK"] = "sommerudsalg" },
            Tags = ["sale", "summer"],
            Intro = new RichTextValue { Markup = "<p>Our biggest sale of the year.</p>" },
            Blocks =
            [
                new Hero
                {
                    Heading = { ["en-US"] = "Summer sale", ["da-DK"] = "Sommerudsalg" },
                    Subheading = { ["en-US"] = "Up to 30% off", ["da-DK"] = "Op til 30% rabat" },
                    Settings = new HeroSettings { Theme = "light" },
                },
                new CardGrid
                {
                    Heading = { ["en-US"] = "Top picks" },
                    Cards =
                    [
                        new Card { Title = "Sandals", Text = "Light and comfy." },
                        new Card { Title = "Sun hats", Text = "Shade for every day." },
                        new Card { Title = "Beach towels", Text = "Big and soft." },
                    ],
                },
            ],
            Promos = { ["en-US"] = [new Promo { Text = "Free shipping over 500" }], ["da-DK"] = [new Promo { Text = "Gratis fragt over 500" }] },
            Image = [new MediaEntry { Media = beach.Id }],
        });

        await site.Pages.SaveModelAsync(new Page
        {
            Key = Guid.NewGuid(),
            Title = { ["en-US"] = "About us", ["da-DK"] = "Om os" },
            UrlSegment = { ["en-US"] = "about-us", ["da-DK"] = "om-os" },
            Grid =
            [
                new RichText
                {
                    Text = { ["en-US"] = "We have sold summer clothes since 1998.", ["da-DK"] = "Vi har solgt sommertøj siden 1998." },
                    Layout = new GridPlacement { ColumnSpan = 12 },
                },
            ],
            Image = [new MediaEntry { Media = team.Id }],
        });
    }
}

/// <summary>
/// A real Umbraco site, reached the way an MCP client reaches it: through the MCP proxy, which lists the site's
/// abilities over the Management API and signs in as its API user. The model's tools are the proxy's MCP tools. After
/// the run, the person's check and apply go through the same proxy, and the person answers yes when it asks to apply.
/// </summary>
internal sealed class McpTarget : RunTarget
{
    private readonly McpClient _client;
    private readonly string _siteUrl;
    private bool _personAnswering;

    private McpTarget(McpClient client, string siteUrl, IReadOnlyList<AIFunction> tools)
    {
        _client = client;
        _siteUrl = siteUrl;
        Tools = tools;
    }

    public override string Name => $"{_siteUrl}, through the MCP proxy";

    public override IReadOnlyList<AIFunction> Tools { get; }

    public static async Task<McpTarget> CreateAsync(string siteUrl)
    {
        // The built proxy, started straight from its dll: "dotnet run" checks the build first, which is slow when
        // several runs start at once.
        var proxy = Path.Combine(FindRepoRoot(), "tools", "Umbraco.Abilities.McpProxy", "bin", "Debug", "net10.0", "Umbraco.Abilities.McpProxy.dll");
        if (!File.Exists(proxy))
        {
            throw new InvalidOperationException($"Build the MCP proxy first: {proxy} is missing.");
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "umbraco-abilities",
            Command = "dotnet",
            Arguments = [proxy],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["UMBRACO_BASE_URL"] = siteUrl,
                ["UMBRACO_CLIENT_ID"] = Setting("UMBRACO_CLIENT_ID"),
                ["UMBRACO_CLIENT_SECRET"] = Setting("UMBRACO_CLIENT_SECRET"),
            },
        });

        // The proxy asks the person before a call that can't be undone. During the run that's the model's call, so the
        // answer is no; after it, the person is applying, so the answer is yes.
        McpTarget? target = null;
        var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    var yes = target?._personAnswering == true;
                    Console.WriteLine($"  ? {request?.Message} → {(yes ? "the person says yes" : "declined: the model can't agree for the person")}");
                    return ValueTask.FromResult(new ElicitResult { Action = yes ? "accept" : "decline" });
                },
            },
        });

        var tools = await client.ListToolsAsync();
        target = new McpTarget(client, siteUrl, tools.Cast<AIFunction>().ToList());
        return target;
    }

    public override async Task ValidateAndApplyAsync()
    {
        var validation = await CallAsync("changeSet_validate", new Dictionary<string, object?>());
        Console.WriteLine($"Validate: {Shorten(validation?.ToJsonString() ?? "no result", 1500)}");

        _personAnswering = true;
        var applied = await CallAsync("changeSet_apply", new Dictionary<string, object?>());
        Console.WriteLine($"Applied by a person: {applied?["status"]} {applied?["message"]}");

        Console.WriteLine();
        Console.WriteLine("── Live entities after apply ──");
        foreach (var saved in applied?["result"] as JsonArray ?? [])
        {
            var entity = await CallAsync("entity_get", new Dictionary<string, object?>
            {
                ["entityType"] = saved!["entityType"]!.GetValue<string>(),
                ["key"] = saved["key"]!.GetValue<string>(),
            });
            Console.WriteLine((entity?["result"] ?? entity)?.ToJsonString(Indented));
        }
    }

    public override async ValueTask DisposeAsync() => await _client.DisposeAsync();

    // Calls a tool through the proxy and returns the ability's own result, or the proxy's error as text.
    private async Task<JsonNode?> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await _client.CallToolAsync(tool, arguments);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private static string Setting(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name}.");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Umbraco.Abilities.Poc.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find the repo root, to start the MCP proxy.");
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
