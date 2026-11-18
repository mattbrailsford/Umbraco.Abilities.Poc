using System.Diagnostics;
using System.Text.Json;

using Anthropic;

using Microsoft.Extensions.AI;

using Umbraco.Abilities;
using Umbraco.Abilities.AI;
using Umbraco.Abilities.Samples;
using Umbraco.Abilities.Samples.Cms;

// A real model builds an entity from a description, using only the generic tools every adapter gets.
// The prompt below names no product, entity field or item type: everything the model learns comes from the tools.
//
//   dotnet run --project tools/Umbraco.Abilities.ModelRun -- "<description>" [entityType] [create|edit]
//
// "edit" asks the model to change entities that already exist, instead of creating one. The site starts with a few
// pages and media items (see SeedContent below), so there is something to find, link to and edit.
//
// Needs ANTHROPIC_API_KEY. Each run calls the Claude API, so it costs money.

var description = args.Length > 0 ? args[0] : "10% off the Summer collection for VIP customers in December";
var entityType = args.Length > 1 ? args[1] : "umbraco-commerce-discount";
var edit = args.Length > 2 && args[2] == "edit";
const string model = "claude-opus-5-5";

var site = SampleSite.Create();
await SeedContent(site);

// No change set is open at the start: the model creates one, and it becomes the conversation's current set. Applying
// is left to a person, so the model doesn't get apply or discard.
var session = new AbilityToolSession(SampleAccess.Editor);
var surfaceOwned = new[] { "changeSet_apply", "changeSet_discard" };
var tools = AbilityAITools
    .Create(site.Abilities, site.Invoker, session)
    .Where(t => !surfaceOwned.Contains(t.Name))
    .Select(t => (AITool)new LoggingFunction(t))
    .ToList();

var system = $"""
    You create and edit entities in a content management system from a person's description.
    Today is {DateTime.UtcNow:dd-MM-yyyy}. Make every change in a change set, so it is held for review and nothing goes
    live until a person applies it. You can't apply it yourself.

    Work like this:
    1. Read the entity type's schema first. Fetch the details of the item types you plan to use before adding them.
    2. Turn names the person used into keys with the find tool. Don't invent keys.
    3. Build or change the entity step by step, then validate the change set and fix every problem it reports.
    4. Finish with a short summary of what you built or changed, and every assumption you made.
    """;

IChatClient chat = new AnthropicClient()
    .AsIChatClient(model)
    .AsBuilder()
    .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 40)
    .Build();

var options = new ChatOptions { Tools = tools, MaxOutputTokens = 16000 };
var messages = new List<ChatMessage>
{
    new(ChatRole.System, system),
    new(ChatRole.User, edit ? $"Edit existing {entityType} entities: {description}" : $"Create a {entityType} entity: {description}"),
};

Console.WriteLine($"Model:       {model}");
Console.WriteLine($"Entity type: {entityType}");
Console.WriteLine($"Mode:        {(edit ? "edit" : "create")}");
Console.WriteLine($"Description: {description}");
Console.WriteLine();

var clock = Stopwatch.StartNew();
var response = await chat.GetResponseAsync(messages, options);
clock.Stop();

Console.WriteLine();
Console.WriteLine("── Model's final message ──");
Console.WriteLine(response.Text);
Console.WriteLine();
Console.WriteLine($"Finish reason: {response.FinishReason}. Tool calls: {LoggingFunction.Calls} ({LoggingFunction.Failures} failed). " +
    $"Time: {clock.Elapsed.TotalSeconds:0}s. Tokens in/out: {response.Usage?.InputTokenCount}/{response.Usage?.OutputTokenCount}.");

// What a person would see before approving, then approve it as they would.
Console.WriteLine();
Console.WriteLine("── Change set check ──");
if (session.ChangeSetId is not { } setId)
{
    Console.WriteLine($"The model left {session.OpenChangeSetIds.Count} change sets open, not one.");
    return;
}

Console.WriteLine($"Current change set: {site.ChangeSets.Get(setId).Name}");
var validation = await site.ChangeSets.ValidateAsync(setId);
var problems = validation.SelectMany(e => e.Problems).ToList();
Console.WriteLine(problems.Count == 0 ? "Valid." : string.Join(Environment.NewLine, problems.Select(p => $"  {p.Path}: {p}")));

var applied = await site.ChangeSets.ApplyAsync(setId, new AbilityContext(session.UserKey));
Console.WriteLine($"Applied by a person: {applied.Status} {applied.Message}");

Console.WriteLine();
Console.WriteLine("── Live entities after apply ──");
foreach (var saved in applied.Result as IEnumerable<EntitySummary> ?? [])
{
    var entity = await site.Adapters.Get(saved.EntityType).LoadAsync(saved.Key);
    Console.WriteLine(entity?.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

// A small site to work on: two pages in English and Danish, and two images. Saved the way an editor's save would be.
static async Task SeedContent(SampleSite site)
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

/// <summary>Prints each tool call and its outcome, so the run reads as a transcript.</summary>
internal sealed class LoggingFunction(AIFunction inner) : DelegatingAIFunction(inner)
{
    public static int Calls { get; private set; }

    public static int Failures { get; private set; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        Calls++;
        var args = JsonSerializer.Serialize(arguments.ToDictionary(a => a.Key, a => a.Value));
        Console.WriteLine($"→ {Name} {Shorten(args, 400)}");

        var result = await base.InvokeCoreAsync(arguments, cancellationToken);
        var json = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
        if (json.TryGetProperty("success", out var ok) && !ok.GetBoolean())
        {
            Failures++;
            Console.WriteLine($"  ✗ {Shorten(json.ToString(), 600)}");
        }
        else
        {
            Console.WriteLine($"  ✓ {Shorten(json.ToString(), 200)}");
        }

        return result;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
