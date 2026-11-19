using Umbraco.Abilities.Consumers;

namespace Umbraco.Abilities.AI;

/// <summary>
/// A chat with a model, such as Umbraco.AI's, as a consumer of abilities: it keeps a conversation, so a change set stays
/// open between calls, and pauses to ask the person before what can't be undone.
/// </summary>
public sealed class AiChatConsumer : IAbilityConsumer
{
    /// <summary>The consumer's alias.</summary>
    public const string ConsumerAlias = "ai-chat";

    public string Alias => ConsumerAlias;

    public string Name => "AI chat";

    public string Description => "A chat with a model. It keeps a conversation, and asks the person before anything that can't be undone.";

    /// <summary>A model reads the tools, so it gets model hints.</summary>
    public string Audience => AbilityAudiences.Model;

    /// <summary>A change set stays open across the conversation, and the chat can stop to ask the person.</summary>
    public IReadOnlyCollection<string> Capabilities { get; } = [ConsumerCapabilities.Conversation, ConsumerCapabilities.AsksConsent];

    /// <summary>The framework's policy for models. Nothing of its own.</summary>
    public IReadOnlyList<string> Filters { get; } = [FrameworkFilters.ForModels];
}
