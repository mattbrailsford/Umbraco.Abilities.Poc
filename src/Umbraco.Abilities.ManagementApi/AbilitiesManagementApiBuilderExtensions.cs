using Umbraco.Abilities.Consumers;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities.ManagementApi;

/// <summary>Adds the consumer HTTP tool callers are.</summary>
public static class AbilitiesManagementApiBuilderExtensions
{
    /// <summary>Adds the MCP server's consumer, so the abilities endpoints can serve it. Call with <c>AddAbilities()</c>.</summary>
    public static IUmbracoBuilder AddAbilitiesManagementApi(this IUmbracoBuilder builder)
    {
        builder.AbilityConsumers().Add<McpConsumer>();
        return builder;
    }
}
