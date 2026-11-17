using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities.AI;

/// <summary>
/// Exposes one ability as an M.E.AI tool. Everything about the tool (its schema, the change set argument and rules, the
/// result) is the framework's <see cref="AbilityTool"/>. This only turns M.E.AI's arguments into JSON and back.
/// </summary>
public sealed class AbilityAIFunction : AIFunction
{
    private readonly AbilityTool _tool;

    public AbilityAIFunction(IAbility ability, AbilityInvoker invoker, AbilityToolSession session)
    {
        _tool = new AbilityTool(ability, invoker, session);
        AdditionalProperties = new Dictionary<string, object?>
        {
            ["abilityId"] = ability.Descriptor.Id,
            ["kind"] = ability.Descriptor.Kind.ToString(),
            ["requiresApproval"] = ability.Descriptor.RequiresApproval,
        };
    }

    public override string Name => _tool.Name;

    public override string Description => _tool.Description;

    public override JsonElement JsonSchema => _tool.JsonSchema;

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
        return (await _tool.InvokeAsync(input, approved: true, cancellationToken)).ToJson();
    }
}

public static class AbilityAITools
{
    /// <summary>
    /// Every ability as a tool, for one conversation. Abilities that need approval are wrapped so a chat client pauses
    /// for a person, the same mechanism Umbraco.AI's HITL approval uses.
    /// </summary>
    public static IReadOnlyList<AIFunction> Create(AbilityRegistry abilities, AbilityInvoker invoker, AbilityToolSession session)
        => abilities.All.Select(ability =>
        {
            AIFunction function = new AbilityAIFunction(ability, invoker, session);
#pragma warning disable MEAI001 // Approval support in M.E.AI is experimental.
            return ability.Descriptor.RequiresApproval ? new ApprovalRequiredAIFunction(function) : function;
#pragma warning restore MEAI001
        }).ToList();
}
