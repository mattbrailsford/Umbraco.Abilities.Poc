using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities;

/// <summary>
/// The abilities packages add on an Umbraco site. The site's <see cref="AbilityRegistry"/> holds them beside the
/// framework's own entity and change set abilities.
/// </summary>
public sealed class AbilityCollection(Func<IEnumerable<IAbility>> items) : BuilderCollectionBase<IAbility>(items);

/// <summary>
/// Builds <see cref="AbilityCollection"/>. A package adds its abilities from its composer or builder extension:
/// <c>builder.Abilities().Add&lt;ActivateDiscountAbility&gt;()</c>. Each ability is made once, by dependency injection,
/// so it can ask for any service, the change set service included.
/// </summary>
public sealed class AbilityCollectionBuilder
    : LazyCollectionBuilderBase<AbilityCollectionBuilder, AbilityCollection, IAbility>
{
    protected override AbilityCollectionBuilder This => this;
}

public static class AbilityCollectionBuilderExtensions
{
    /// <summary>The abilities packages add, to add or exclude one.</summary>
    public static AbilityCollectionBuilder Abilities(this IUmbracoBuilder builder)
        => builder.WithCollectionBuilder<AbilityCollectionBuilder>();
}
