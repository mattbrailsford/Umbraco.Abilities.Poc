using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Commerce.Core.Api;

namespace Umbraco.Abilities.Commerce;

/// <summary>Stores, so a discount can point at one. Read only: stores are set up in the backoffice.</summary>
public sealed class CommerceStoreAdapter(IUmbracoCommerceApi commerce, CommerceAccess access, ItemTypeRegistry itemTypes)
    : EntityAdapterBase<StoreModel>(itemTypes, new CommerceUnitOfWorkProvider(commerce))
{
    public const string Type = "umbraco-commerce-store";

    public override bool CanCreate => false;

    public override async Task<StoreModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
        => await commerce.GetStoreAsync(key) is { } store ? new StoreModel { Key = store.Id, Name = store.Name, Alias = store.Alias } : null;

    public override Task SaveModelAsync(StoreModel model, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stores are set up in the backoffice.");

    public override async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => await access.CheckAsync(request.UserKey, await commerce.GetStoreAsync(request.Key));

    protected override async Task<IReadOnlyList<StoreModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
        => (await commerce.GetStoresAsync())
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Alias.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => new StoreModel { Key = s.Id, Name = s.Name, Alias = s.Alias })
            .ToList();

    protected override string? GetEditUrl(Guid key) => $"/umbraco/section/commerce/workspace/uc:store/edit/{key}";
}
