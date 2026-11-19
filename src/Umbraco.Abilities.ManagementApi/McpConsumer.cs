using Umbraco.Abilities.Consumers;

namespace Umbraco.Abilities.ManagementApi;

/// <summary>
/// A model through an MCP server, such as Umbraco CMS MCP Dev, as a consumer of abilities. Its session keeps a change set
/// open, and its client can ask the person before what can't be undone. A caller that names no consumer is this one.
/// </summary>
public sealed class McpConsumer : IAbilityConsumer
{
    /// <summary>The consumer's alias.</summary>
    public const string ConsumerAlias = "mcp";

    public string Alias => ConsumerAlias;

    public string Name => "MCP server";

    public string Description => "A model through an MCP server, such as Umbraco CMS MCP Dev. Its session keeps a change set open, and its client asks the person before anything that can't be undone.";

    /// <summary>A model reads the tools, so it gets model hints.</summary>
    public string Audience => AbilityAudiences.Model;

    /// <summary>The MCP session keeps a change set open, and the client can ask the person (elicitation).</summary>
    public IReadOnlyCollection<string> Capabilities { get; } = [ConsumerCapabilities.Conversation, ConsumerCapabilities.AsksConsent];

    /// <summary>The framework's policy for models. Nothing of its own.</summary>
    public IReadOnlyList<string> Filters { get; } = [FrameworkFilters.ForModels];
}
