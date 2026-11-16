using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Abilities.Cms.PropertyEditors;

/// <summary>Packages' property editor handlers. The core editors' handlers are the document adapter's own.</summary>
public sealed class PropertyEditorEntityHandlerCollection(Func<IEnumerable<IPropertyEditorEntityHandler>> items)
    : BuilderCollectionBase<IPropertyEditorEntityHandler>(items);

/// <summary>
/// Builds <see cref="PropertyEditorEntityHandlerCollection"/>. Every handler in a scanned assembly is found on its own
/// (<see cref="PropertyEditorEntityHandlersComposer"/>), so a package that ships one needs no registration. Use
/// <c>Add</c> or <c>Exclude</c> to change that.
/// </summary>
public sealed class PropertyEditorEntityHandlerCollectionBuilder
    : LazyCollectionBuilderBase<PropertyEditorEntityHandlerCollectionBuilder, PropertyEditorEntityHandlerCollection, IPropertyEditorEntityHandler>
{
    protected override PropertyEditorEntityHandlerCollectionBuilder This => this;
}

public static class PropertyEditorEntityHandlerBuilderExtensions
{
    /// <summary>The property editor handlers, to add or exclude one.</summary>
    public static PropertyEditorEntityHandlerCollectionBuilder PropertyEditorEntityHandlers(this IUmbracoBuilder builder)
        => builder.WithCollectionBuilder<PropertyEditorEntityHandlerCollectionBuilder>();
}

/// <summary>
/// Finds every <see cref="IPropertyEditorEntityHandler"/> in the scanned assemblies, except the core ones the document
/// adapter adds itself.
/// </summary>
public sealed class PropertyEditorEntityHandlersComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
        => builder.PropertyEditorEntityHandlers()
            .Add(() => builder.TypeLoader.GetTypes<IPropertyEditorEntityHandler>())
            .Exclude<ValueEditorHandler>()
            .Exclude<BlockEditorHandler>()
            .Exclude<JsonValueEditorHandler>();
}
