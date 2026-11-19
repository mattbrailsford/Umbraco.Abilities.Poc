using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities.Consumers;

/// <summary>
/// The filters packages add on an Umbraco site. The site's <see cref="AbilityFilterRegistry"/> holds them beside the
/// framework's own.
/// </summary>
public sealed class AbilityFilterCollection(Func<IEnumerable<IAbilityFilter>> items) : BuilderCollectionBase<IAbilityFilter>(items);

/// <summary>
/// Builds <see cref="AbilityFilterCollection"/>. A package adds its filters from its composer or builder extension:
/// <c>builder.AbilityFilters().Add&lt;AutomateEditorsFilter&gt;()</c>. Each filter is made once, by dependency
/// injection. The order doesn't matter: a consumer names its filters, in its own order.
/// </summary>
public sealed class AbilityFilterCollectionBuilder
    : LazyCollectionBuilderBase<AbilityFilterCollectionBuilder, AbilityFilterCollection, IAbilityFilter>
{
    protected override AbilityFilterCollectionBuilder This => this;
}

public static class AbilityFilterCollectionBuilderExtensions
{
    /// <summary>The filters packages add, to add or exclude one.</summary>
    public static AbilityFilterCollectionBuilder AbilityFilters(this IUmbracoBuilder builder)
        => builder.WithCollectionBuilder<AbilityFilterCollectionBuilder>();
}
