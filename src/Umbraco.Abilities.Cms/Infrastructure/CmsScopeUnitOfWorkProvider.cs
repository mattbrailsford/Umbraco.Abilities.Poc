using Umbraco.Abilities.ChangeSets;
using Umbraco.Cms.Core.Scoping;

namespace Umbraco.Abilities.Cms;

/// <summary>Content's step of applying a change set runs in one Umbraco scope, so every document saves or none does.</summary>
public sealed class CmsScopeUnitOfWorkProvider(ICoreScopeProvider scopes) : IUnitOfWorkProvider
{
    public string Name => "Content";

    public async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateCoreScope();
        await work(cancellationToken);
        scope.Complete();
    }
}
