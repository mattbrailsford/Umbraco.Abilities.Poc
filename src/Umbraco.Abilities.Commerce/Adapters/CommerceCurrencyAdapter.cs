using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Models;

namespace Umbraco.Abilities.Commerce;

/// <summary>
/// A store's currencies, so a model can fill in an amount, which Commerce keys by currency. Read only: currencies are
/// set up in the backoffice.
/// </summary>
public sealed class CommerceCurrencyAdapter(IUmbracoCommerceApi commerce, CommerceAccess access, ItemTypeRegistry itemTypes)
    : EntityAdapterBase<CurrencyModel>(itemTypes, new CommerceUnitOfWorkProvider(commerce))
{
    public const string Type = "umbraco-commerce-currency";

    public override bool CanCreate => false;

    public override async Task<CurrencyModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
        => await commerce.GetCurrencyAsync(key) is { } currency ? ToModel(currency) : null;

    public override Task SaveModelAsync(CurrencyModel model, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Currencies are set up in the backoffice.");

    public override async Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => await access.CheckAsync(request.UserKey, await commerce.GetCurrencyAsync(request.Key) is { } c ? await commerce.GetStoreAsync(c.StoreId) : null);

    protected override async Task<IReadOnlyList<CurrencyModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
    {
        var found = new List<CurrencyModel>();
        foreach (var store in await commerce.GetStoresAsync())
        {
            found.AddRange((await commerce.GetCurrenciesAsync(store.Id))
                .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Code.Contains(query, StringComparison.OrdinalIgnoreCase) || store.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(ToModel));
        }

        return found;
    }

    // Currencies belong to a store, as discounts do.
    protected override async Task<EntityGroup?> GetGroupAsync(CurrencyModel model, CancellationToken cancellationToken)
        => model.Store is { } key && await commerce.GetStoreAsync(key) is { } store ? new EntityGroup(CommerceStoreAdapter.Type, store.Id, store.Name) : null;

    protected override string? GetEditUrl(Guid key) => null;

    private static CurrencyModel ToModel(CurrencyReadOnly currency)
        => new() { Key = currency.Id, Name = currency.Name, Code = currency.Code, Store = currency.StoreId };
}
