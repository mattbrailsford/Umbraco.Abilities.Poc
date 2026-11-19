using Umbraco.Abilities.ChangeSets;
using Umbraco.Commerce.Core.Api;

namespace Umbraco.Abilities.Commerce;

/// <summary>
/// Commerce's transaction, for a step of applying a change set. Commerce's unit of work only runs a callback, and may
/// retry it, which is the shape <see cref="IUnitOfWorkProvider.RunAsync"/> has.
/// </summary>
public sealed class CommerceUnitOfWorkProvider(IUmbracoCommerceApi commerce) : IUnitOfWorkProvider
{
    public string Name => "Commerce";

    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            await work(ct);
            uow.Complete();
        }, cancellationToken);
}
