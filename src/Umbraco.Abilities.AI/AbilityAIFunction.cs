using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

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
