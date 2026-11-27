using System.Text.Json;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Models;

namespace Umbraco.Abilities.Demo;

/// <summary>
/// Adds a small shop to the Clean starter kit with real Umbraco Commerce: a store (Commerce fills in its currency,
/// country, tax class, statuses and methods), its store picker on the home page, a Shop page and three products with
/// prices, and a "Welcome" code discount to give out codes from (an Automate step's "Add a discount code"). Each part is
/// made only if missing, so it is safe on every start.
/// </summary>
public sealed class DemoShop(
    IUmbracoCommerceApi commerce,
    IDataTypeService dataTypes,
    IContentTypeService contentTypes,
    IContentService content,
    PropertyEditorCollection editors,
    IConfigurationEditorJsonSerializer serializer,
    IShortStringHelper shortStrings,
    IUserGroupService userGroups,
    ILogger<DemoShop> logger)
{
    /// <summary>The demo store's alias.</summary>
    public const string StoreAlias = "demo";

    private static readonly (string Name, string Sku, decimal Price)[] Products =
    [
        ("Sun hat", "SUN-HAT", 25m),
        ("Beach towel", "BEACH-TOWEL", 18m),
        ("Sunglasses", "SUNGLASSES", 60m),
    ];

    /// <summary>Makes what is missing. Run by <see cref="DemoSeeder"/> once Umbraco is up.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var store = await EnsureStoreAsync(cancellationToken);
        await EnsureEditorsCanUseStoreAsync(store, cancellationToken);
        await EnsureWelcomeDiscountAsync(store, cancellationToken);
        var storePicker = await EnsureDataTypeAsync("Umbraco Commerce: Store Picker", "Umbraco.Commerce.StorePicker", "Uc.PropertyEditorUi.StorePicker", ValueStorageType.Nvarchar);
        var price = await EnsureDataTypeAsync("Umbraco Commerce: Price", "Umbraco.Commerce.Price", "Uc.PropertyEditorUi.Price", ValueStorageType.Ntext);
        var textbox = await dataTypes.GetAsync(Constants.DataTypes.Guids.TextstringGuid);

        if (contentTypes.Get("home") is not { } homeType || content.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "home") is not { } home)
        {
            logger.LogWarning("Demo shop: no Clean home page yet, so no shop. It is added on the next start.");
            return;
        }

        var productType = await EnsureContentTypeAsync("product", "Product", "icon-tag", [("price", "Price", price), ("sku", "SKU", textbox!)], []);
        var shopType = await EnsureContentTypeAsync("shop", "Shop", "icon-shopping-basket-alt", [], [productType]);

        // The store is found from the home page, as Commerce reads it from a product's ancestors.
        if (!homeType.PropertyTypeExists("store"))
        {
            homeType.AddPropertyType(new PropertyType(shortStrings, storePicker, "store") { Name = "Store" });
        }

        if (!homeType.AllowedContentTypes!.Any(t => t.Key == shopType.Key))
        {
            homeType.AllowedContentTypes = [.. homeType.AllowedContentTypes!, new ContentTypeSort(shopType.Key, homeType.AllowedContentTypes!.Count(), shopType.Alias)];
        }

        await contentTypes.UpdateAsync(homeType, Constants.Security.SuperUserKey);

        home = content.GetById(home.Key)!;
        if (home.GetValue<string>("store") != store.Id.ToString())
        {
            home.SetValue("store", store.Id.ToString());
            content.Save(home);
        }

        var shop = content.GetPagedChildren(home.Id, 0, 100, out _).FirstOrDefault(c => c.ContentType.Alias == "shop");
        if (shop is null)
        {
            shop = content.Create("Shop", home.Key, "shop");
            content.Save(shop);
            foreach (var (name, sku, amount) in Products)
            {
                var product = content.Create(name, shop.Key, "product");
                product.SetValue("sku", sku);
                product.SetValue("price", JsonSerializer.Serialize(new Dictionary<Guid, decimal> { [store.BaseCurrencyId!.Value] = amount }));
                content.Save(product);
            }

            logger.LogInformation("Demo shop: added the Shop page and {Count} products.", Products.Length);
        }
    }

    private async Task<StoreReadOnly> EnsureStoreAsync(CancellationToken cancellationToken)
    {
        if (await commerce.GetStoreAsync(StoreAlias) is { } existing)
        {
            return existing;
        }

        // Commerce fills a new store in: currency, country, tax class, order statuses, payment and shipping methods.
        var store = await commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            var created = await Store.CreateAsync(uow, StoreAlias, "Demo store");
            await commerce.SaveStoreAsync(created, ct);
            uow.Complete();
            return created.AsReadOnly();
        }, cancellationToken);
        logger.LogInformation("Demo shop: created the store '{Alias}'.", StoreAlias);
        return (await commerce.GetStoreAsync(store.Id))!;
    }

    // 10% off the order with a code. Set up once, as a person would; an automation then gives out codes for it.
    private async Task EnsureWelcomeDiscountAsync(StoreReadOnly store, CancellationToken cancellationToken)
    {
        if ((await commerce.GetDiscountsAsync(store.Id)).Any(d => d.Alias == "welcome"))
        {
            return;
        }

        await commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            var discount = await Discount.CreateAsync(uow, Guid.NewGuid(), store.Id, "welcome", "Welcome 10%");
            await discount.SetTypeAsync(DiscountType.Code);
            await discount.AddCodesAsync([new DiscountCode("WELCOME10", null, true)]);
            await discount.SetRulesAsync(new DiscountRuleConfig("groupDiscountRule", [KeyValuePair.Create("matchType", "All")], []), false, false);
            await discount.SetRewardsAsync([new DiscountRewardConfig("orderAmountDiscountReward", new Dictionary<string, string>
            {
                ["priceType"] = "SubtotalPrice",
                ["adjustmentType"] = "Percentage",
                ["percentage"] = "10",
            })]);
            await discount.SetActiveAsync(true);
            await commerce.SaveDiscountAsync(discount, ct);
            uow.Complete();
        }, cancellationToken);
        logger.LogInformation("Demo shop: added the Welcome 10% code discount.");
    }

    // Editors (the MCP API user's group) and administrators get the Commerce section and the store, so discounts
    // can be made through the abilities. Commerce allows a store to users and groups by name.
    private async Task EnsureEditorsCanUseStoreAsync(StoreReadOnly store, CancellationToken cancellationToken)
    {
        Guid[] groupKeys = [Constants.Security.EditorGroupKey, Constants.Security.AdminGroupKey];
        foreach (var key in groupKeys)
        {
            if (await userGroups.GetAsync(key) is { } group && !group.AllowedSections.Contains("commerce"))
            {
                group.AddAllowedSection("commerce");
                await userGroups.UpdateAsync(group, Constants.Security.SuperUserKey);
            }
        }

        var roles = groupKeys.Select(k => k.ToString()).ToList();
        if (roles.All(r => store.IsAllowed(string.Empty, [r])))
        {
            return;
        }

        await commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            var writable = await store.AsWritableAsync(uow);
            await writable.AllowUserRolesAsync(roles);
            await commerce.SaveStoreAsync(writable, ct);
            uow.Complete();
        }, cancellationToken);
        logger.LogInformation("Demo shop: editors and administrators can use the store.");
    }

    private async Task<IDataType> EnsureDataTypeAsync(string name, string editorAlias, string editorUiAlias, ValueStorageType storage)
    {
        if ((await dataTypes.GetByEditorAliasAsync(editorAlias)).FirstOrDefault() is { } existing)
        {
            return existing;
        }

        var dataType = new DataType(editors[editorAlias]!, serializer)
        {
            Name = name,
            EditorUiAlias = editorUiAlias,
            DatabaseType = storage,
        };
        return (await dataTypes.CreateAsync(dataType, Constants.Security.SuperUserKey)).Result;
    }

    private async Task<IContentType> EnsureContentTypeAsync(string alias, string name, string icon, (string Alias, string Name, IDataType DataType)[] properties, IContentType[] allowedChildren)
    {
        if (contentTypes.Get(alias) is { } existing)
        {
            return existing;
        }

        var type = new ContentType(shortStrings, -1) { Alias = alias, Name = name, Icon = icon };
        foreach (var (propertyAlias, propertyName, dataType) in properties)
        {
            type.AddPropertyType(new PropertyType(shortStrings, dataType, propertyAlias) { Name = propertyName }, "content", "Content");
        }

        type.AllowedContentTypes = allowedChildren.Select((child, i) => new ContentTypeSort(child.Key, i, child.Alias)).ToList();
        await contentTypes.CreateAsync(type, Constants.Security.SuperUserKey);
        return contentTypes.Get(alias)!;
    }
}
