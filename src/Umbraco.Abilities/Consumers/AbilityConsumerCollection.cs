using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities.Consumers;

/// <summary>The consumers on an Umbraco site. The site's <see cref="AbilityConsumerRegistry"/> holds them.</summary>
public sealed class AbilityConsumerCollection(Func<IEnumerable<IAbilityConsumer>> items) : BuilderCollectionBase<IAbilityConsumer>(items);

/// <summary>
/// Builds <see cref="AbilityConsumerCollection"/>. A package adds its consumer from its composer or builder extension:
/// <c>builder.AbilityConsumers().Add&lt;McpConsumer&gt;()</c>. Each consumer is made once, by dependency injection.
/// </summary>
public sealed class AbilityConsumerCollectionBuilder
    : LazyCollectionBuilderBase<AbilityConsumerCollectionBuilder, AbilityConsumerCollection, IAbilityConsumer>
{
    protected override AbilityConsumerCollectionBuilder This => this;
}

public static class AbilityConsumerCollectionBuilderExtensions
{
    /// <summary>The consumers, to add or exclude one.</summary>
    public static AbilityConsumerCollectionBuilder AbilityConsumers(this IUmbracoBuilder builder)
        => builder.WithCollectionBuilder<AbilityConsumerCollectionBuilder>();
}
