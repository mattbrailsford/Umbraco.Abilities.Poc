using System.Diagnostics;
using System.Text.Json;

using Anthropic;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.ModelRun;

// A real model builds an entity from a description, using only the generic tools every adapter gets.
// The prompt below names no product, entity field or item type: everything the model learns comes from the tools.
//
//   dotnet run --project tools/Umbraco.Abilities.ModelRun -- "<description>" [entityType] [create|edit]
//
// "edit" asks the model to change entities that already exist, instead of creating one.
//
// By default it runs against the in-memory samples, which start with a few pages and media items (see SampleTarget),
// so there is something to find, link to and edit. Set UMBRACO_BASE_URL, UMBRACO_CLIENT_ID and UMBRACO_CLIENT_SECRET
// to run against a real Umbraco site instead, such as the demo site: the tools then come from the MCP proxy, as an MCP
// client would get them (see McpTarget).
//
// Needs ANTHROPIC_API_KEY. Each run calls the Claude API, so it costs money.

var description = args.Length > 0 ? args[0] : "10% off the Summer collection for VIP customers in December";
var entityType = args.Length > 1 ? args[1] : "umbraco-commerce-discount";
var edit = args.Length > 2 && args[2] == "edit";
const string model = "claude-opus-5-5";

await using RunTarget target = Environment.GetEnvironmentVariable("UMBRACO_BASE_URL") is { Length: > 0 } siteUrl
    ? await McpTarget.CreateAsync(siteUrl)
    : await SampleTarget.CreateAsync();

// No change set is open at the start: the model creates one, and it becomes the conversation's current set. Applying
// is left to a person, so the model doesn't get apply or discard.
var surfaceOwned = new[] { "changeSet_apply", "changeSet_discard" };
var tools = target.Tools
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
Console.WriteLine($"Site:        {target.Name}");
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
Console.WriteLine("── Change set check, and a person applies it ──");
await target.ValidateAndApplyAsync();

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

        // An ability's own result says success; an MCP tool's result says isError.
        var failed = (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("success", out var ok) && !ok.GetBoolean())
            || (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True);
        if (failed)
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
