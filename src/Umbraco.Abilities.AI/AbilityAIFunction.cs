using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Consumers;

namespace Umbraco.Abilities.AI;

/// <summary>
/// Exposes one tool as an M.E.AI tool. Which tools there are, and their schemas, are the AI chat consumer's (see
/// <see cref="AbilityAITools"/>); the runner runs the calls. This only turns M.E.AI's arguments into JSON and back.
/// </summary>
public sealed class AbilityAIFunction : AIFunction
{
    private readonly AbilityTool _tool;
    private readonly AbilityToolProvider _tools;
    private readonly string _consumer;
    private readonly AbilityToolRunner _runner;
    private readonly AbilityToolSession _session;

    public AbilityAIFunction(AbilityTool tool, AbilityToolProvider tools, string consumer, AbilityToolRunner runner, AbilityToolSession session)
    {
        _tool = tool;
        _tools = tools;
        _consumer = consumer;
        _runner = runner;
        _session = session;
        AdditionalProperties = new Dictionary<string, object?>
        {
            ["abilityId"] = tool.Descriptor.Id,
            ["kind"] = tool.Descriptor.Kind.ToString(),
            ["requiresApproval"] = tool.Descriptor.RequiresApproval,
        };
    }

    public override string Name => _tool.Name;

    public override string Description => _tool.Description;

    /// <summary>The tool's input schema, as the consumer's filters shaped it.</summary>
    public override JsonElement JsonSchema => _tool.InputSchema;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; }

    public AbilityDescriptor Descriptor => _tool.Descriptor;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var input = new JsonObject();
        foreach (var (name, value) in arguments)
        {
            input[name] = value switch
            {
                null => null,
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                JsonNode node => node.DeepClone(),
                _ => JsonSerializer.SerializeToNode(value, AbilityJson.Options),
            };
        }

        // Consent is the host's: an ability that needs it is wrapped in ApprovalRequiredAIFunction (see AbilityAITools),
        // and the chat client only calls it once the person approved. Umbraco.AI's approval policy decides the same way:
        // it asks (Interactive), skips the call (DenyAll, DenyApprovalRequired), or was told up front to run it (AllowAll).
        // A result can hold a schema (entity.getSchema): the consumer's filters shape it as they shaped the tool.
        var result = await _runner.InvokeAsync(_tool, input, _session, AbilityConsent.Given, cancellationToken);
        return _tools.ShapeResult(_consumer, _tool, result.ToJson());
    }
}

public static class AbilityAITools
{
    /// <summary>The AI chat consumer's alias.</summary>
    public const string ConsumerAlias = "ai-chat";

    /// <summary>
    /// A chat with a model, such as Umbraco.AI's: it keeps a conversation, so a change set stays open between calls, and
    /// pauses to ask the person before what can't be undone. Its reader is a model.
    /// </summary>
    public static IAbilityConsumer Consumer { get; } = new AbilityConsumer(
        ConsumerAlias,
        "AI chat",
        "A chat with a model. It keeps a conversation, and asks the person before anything that can't be undone.",
        AbilityAudiences.Model,
        [ConsumerCapabilities.Conversation, ConsumerCapabilities.AsksConsent],
        [FrameworkFilters.ForModels]);

    /// <summary>
    /// The consumer's tools as M.E.AI tools, for one conversation. Abilities that need approval are wrapped so a chat
    /// client pauses for a person, the same mechanism Umbraco.AI's HITL approval uses.
    /// </summary>
    public static IReadOnlyList<AIFunction> Create(AbilityToolProvider tools, AbilityToolRunner runner, AbilityToolSession session, string consumer = ConsumerAlias)
        => tools.GetTools(consumer).Select(tool =>
        {
            AIFunction function = new AbilityAIFunction(tool, tools, consumer, runner, session);
#pragma warning disable MEAI001 // Approval support in M.E.AI is experimental.
            return tool.Descriptor.RequiresApproval ? new ApprovalRequiredAIFunction(function) : function;
#pragma warning restore MEAI001
        }).ToList();

    /// <summary>
    /// The tools for a host that isn't a site with its own filters and consumers, such as a test or a console run: the
    /// AI chat consumer, with the framework's filters only.
    /// </summary>
    public static IReadOnlyList<AIFunction> Create(AbilityRegistry abilities, AbilityInvoker invoker, AbilityToolSession session)
        => Create(AbilityToolProvider.CreateDefault(abilities, Consumer), new AbilityToolRunner(invoker), session);
}
