using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities.Entities;

/// <summary>
/// The entity adapters on an Umbraco site: CMS documents and media, and those packages add. The site's
/// <see cref="EntityAdapterRegistry"/> holds them.
/// </summary>
public sealed class EntityAdapterCollection(Func<IEnumerable<IEntityAdapter>> items) : BuilderCollectionBase<IEntityAdapter>(items);

/// <summary>
/// Builds <see cref="EntityAdapterCollection"/>. A package adds its adapters from its composer or builder extension:
/// <c>builder.EntityAdapters().Add&lt;CommerceDiscountAdapter&gt;()</c>. Each adapter is made once, by dependency
/// injection. The order packages add them in doesn't matter.
/// </summary>
public sealed class EntityAdapterCollectionBuilder
    : LazyCollectionBuilderBase<EntityAdapterCollectionBuilder, EntityAdapterCollection, IEntityAdapter>
{
    protected override EntityAdapterCollectionBuilder This => this;
}

public static class EntityAdapterCollectionBuilderExtensions
{
    /// <summary>The entity adapters, to add or exclude one.</summary>
    public static EntityAdapterCollectionBuilder EntityAdapters(this IUmbracoBuilder builder)
        => builder.WithCollectionBuilder<EntityAdapterCollectionBuilder>();
}
