using Microsoft.Extensions.AI;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Consumers;

namespace Umbraco.Abilities.AI;

/// <summary>Turns the AI chat consumer's tools into M.E.AI functions, ready to hand to a chat client.</summary>
public static class AbilityAITools
{
    /// <summary>
    /// The consumer's tools as M.E.AI tools, for one conversation. Abilities that need approval are wrapped so a chat
    /// client pauses for a person, the same mechanism Umbraco.AI's HITL approval uses.
    /// </summary>
    public static IReadOnlyList<AIFunction> Create(AbilityToolProvider tools, AbilityToolRunner runner, AbilityToolSession session, string consumer = AiChatConsumer.ConsumerAlias)
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
        => Create(AbilityToolProvider.CreateDefault(abilities, new AiChatConsumer()), new AbilityToolRunner(invoker), session);
}
