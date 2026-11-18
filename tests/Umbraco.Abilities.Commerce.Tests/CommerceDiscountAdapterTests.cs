using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using NUnit.Framework;

using Umbraco.Abilities.AI;
using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Cms;
using Umbraco.Abilities.Commerce;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Tests.Common.Builders;
using Umbraco.Cms.Tests.Common.Builders.Extensions;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Data;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.Cms.Events.Notification.Handlers;
using Umbraco.Commerce.Extensions;
using Umbraco.Commerce.Persistence.Migrations;

using CommerceDiscount = Umbraco.Commerce.Core.Models.Discount;

namespace Umbraco.Abilities.Commerce.Tests;

/// <summary>
/// The Commerce discount adapter against a real Umbraco Commerce (SQLite): a discount built through the AI tools in a
/// change set, with Commerce's own rule and reward providers, checked by Commerce's own validation, and saved through
/// Commerce's C# API only when the set is applied.
/// </summary>
[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, WithApplication = true)]
public sealed class CommerceDiscountAdapterTests : UmbracoIntegrationTest
{
    private const string Discount = CommerceDiscountAdapter.Type;

    private Agent _agent = null!;
    private StoreReadOnly _store = null!;
    private string _commerceDatabase = string.Empty;

    private IUmbracoCommerceApi Commerce => GetRequiredService<IUmbracoCommerceApi>();

    private IContentService ContentService => GetRequiredService<IContentService>();

    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        // Commerce points a static service locator at the site's services from an Umbraco component. Umbraco skips
        // components while it upgrades, as each test's database does here, so the locator would keep the first test's
        // services, long disposed. Point it at this test's first thing as the host starts.
        builder.Services.Insert(0, ServiceDescriptor.Singleton<IHostedService, PointCommerceAtThisTest>());
        builder.AddUmbracoCommerce(commerce => commerce.AddSQLite());

        // Commerce makes its tables from a starting handler. Here Umbraco may raise that in the background, racing the
        // test, so the test makes them itself once Umbraco has started (see SetUpStoreAsync).
        builder.Services.RemoveAll(d => d.ImplementationType == typeof(RunUmbracoCommerceMigrations));
        builder.AddAbilities();
        builder.AddCommerceAbilities();

        // CMS documents save as the signed-in backoffice user. A test has no request, so they save as the admin.
        builder.Services.AddSingleton(sp => new CmsDocumentAdapter(
            sp.GetRequiredService<CmsContentSchemas>(),
            sp.GetRequiredService<IContentTypeService>(),
            sp.GetRequiredService<IContentEditingService>(),
            sp.GetRequiredService<IEntityService>(),
            sp.GetRequiredService<Umbraco.Cms.DraftSets.DraftSetService>(),
            sp.GetRequiredService<IUserService>(),
            sp.GetRequiredService<IContentPermissionService>(),
            sp.GetRequiredService<CmsScopeUnitOfWorkProvider>(),
            () => Constants.Security.SuperUserKey));
        builder.Services.AddSingleton<StartedSignal>();
        builder.AddNotificationHandler<UmbracoApplicationStartingNotification, StartedSignal.Handler>();

        // Umbraco's test databases are SQLite in memory, which Commerce's migrations (DbUp) don't run on: they make
        // their journal table twice. So Commerce gets a database file of its own for each test. Commerce only refers
        // to Umbraco's data by key, so it doesn't need Umbraco's tables.
        _commerceDatabase = Path.Combine(Path.GetTempPath(), $"abilities-commerce-{Guid.NewGuid():N}.sqlite");
        var database = _commerceDatabase;
        builder.Services.AddOptions<ConnectionStringConfig>().PostConfigure(commerce =>
        {
            commerce.Name = "umbracoCommerceDB";
            commerce.ConnectionString = $"Data Source={database};Foreign Keys=True;Pooling=False";
            commerce.ProviderName = "Microsoft.Data.Sqlite";
        });
    }

    protected override void SetUpTestConfiguration(IConfigurationBuilder configBuilder)
    {
        base.SetUpTestConfiguration(configBuilder);
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?> { ["Tests:Database:DatabaseType"] = "Sqlite" });
    }

    [SetUp]
    public async Task SetUpStoreAsync()
    {
        // Commerce's package migration makes each test database need an upgrade, which Umbraco 18.3 runs in the
        // background, and only then raises the starting notification that registers the adapters. Writing before that
        // deadlocks SQLite, so wait for it. Then make Commerce's tables.
        await GetRequiredService<StartedSignal>().Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        GetRequiredService<IUmbracoCommerceMigrationsRunner>().MigrateUp();

        // A store as Commerce makes one (a currency, a country, a tax class...), which administrators may use.
        var userGroups = GetRequiredService<IUserGroupService>();
        var admins = (await userGroups.GetAsync(Constants.Security.AdminGroupKey))!;
        admins.AddAllowedSection(CommerceAccess.Section);
        await userGroups.UpdateAsync(admins, Constants.Security.SuperUserKey);

        _store = await Commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            var store = await Store.CreateAsync(uow, "shop", "Shop");
            await store.AllowUserRoleAsync(Constants.Security.AdminGroupKey.ToString());
            await Commerce.SaveStoreAsync(store, ct);
            uow.Complete();
            return store.AsReadOnly();
        });

        // A page type for the tests that build content beside a discount: a name and a mandatory title.
        var landingPage = new ContentTypeBuilder()
            .WithAlias("landingPage").WithName("Landing page").WithContentVariation(ContentVariation.Nothing)
            .AddPropertyType()
                .WithAlias("title").WithName("Title")
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(ContentVariation.Nothing).WithMandatory(true)
                .Done()
            .Build();
        landingPage.AllowedAsRoot = true;
        await GetRequiredService<IContentTypeService>().CreateAsync(landingPage, Constants.Security.SuperUserKey);

        _agent = new Agent(GetRequiredService<AbilityRegistry>(), GetRequiredService<AbilityInvoker>());
    }

    [TearDown]
    public void DeleteCommerceDatabase()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        System.IO.File.Delete(_commerceDatabase);
    }

    [Test]
    public async Task A_discount_built_in_a_change_set_is_saved_in_Commerce_only_when_the_set_is_applied()
    {
        var key = Guid.NewGuid();
        await _agent.StartChangeSetAsync("Summer");
        await CreateSummerHatsAsync(key);

        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.True, validation.ToString());
        Assert.That(await Commerce.GetDiscountAsync(key), Is.Null, "nothing is saved before apply");

        await _agent.OkAsync("changeSet_apply", SetJson());

        var saved = (await Commerce.GetDiscountAsync(key))!;
        Assert.Multiple(() =>
        {
            Assert.That(saved.Name, Is.EqualTo("Summer hats"));
            Assert.That(saved.StoreId, Is.EqualTo(_store.Id));
            Assert.That(saved.Alias, Is.EqualTo("summerHats"));
            Assert.That(saved.Type, Is.EqualTo(DiscountType.Code));
            Assert.That(saved.Codes.Select(c => c.Code), Is.EqualTo(new[] { "SUMMER10" }));
            Assert.That(saved.StartDate, Is.EqualTo(new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(saved.ExpiryDate, Is.EqualTo(new DateTime(2027, 8, 31, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(saved.IsActive, Is.True);
            Assert.That(saved.Rules.RuleProviderAlias, Is.EqualTo("groupDiscountRule"));
            Assert.That(saved.Rules.Children.Single().RuleProviderAlias, Is.EqualTo("orderAmountDiscountRule"));
            Assert.That(saved.Rewards.Single().RewardProviderAlias, Is.EqualTo("orderAmountDiscountReward"));
            Assert.That(saved.Rewards.Single().Settings["percentage"], Is.EqualTo("10"));
        });
    }

    [Test]
    public async Task Settings_are_stored_as_Commerces_own_editors_store_them_so_the_backoffice_shows_them()
    {
        var key = Guid.NewGuid();
        await _agent.StartChangeSetAsync("Summer");
        var currency = await CreateSummerHatsAsync(key);
        await _agent.OkAsync("changeSet_apply", SetJson());

        var saved = (await Commerce.GetDiscountAsync(key))!;
        var rule = saved.Rules.Children.Single().Settings;
        var reward = saved.Rewards.Single().Settings;
        Assert.Multiple(() =>
        {
            // An enum is its member name, as Commerce's dropdowns list it; an amount per currency is a number.
            Assert.That(rule["priceType"], Is.EqualTo("TotalPrice"));
            Assert.That(rule["comparisonOperator"], Is.EqualTo("GreaterThanOrEqual"));
            Assert.That(rule["amounts"], Is.EqualTo($$"""{"{{currency}}":50}"""));
            Assert.That(reward["priceType"], Is.EqualTo("SubtotalPrice"));
            Assert.That(reward["adjustmentType"], Is.EqualTo("Percentage"));
        });
    }

    [Test]
    public async Task A_page_and_a_discount_built_in_one_change_set_are_applied_together_each_in_its_products_transaction()
    {
        var page = Guid.NewGuid();
        var discount = Guid.NewGuid();
        await _agent.StartChangeSetAsync("Summer launch");
        await _agent.OkAsync("entity_create", $$"""
            { "entityType": "document", "key": "{{page}}", "subtype": "landingPage", "fields": { "name": "Summer hats", "title": "10% off with SUMMER10" } }
            """);
        await CreateSummerHatsAsync(discount);

        // Both wait in the set, and the plan says how they'll apply: content, then Commerce, each in its own
        // transaction.
        var summary = await _agent.OkAsync("changeSet_summarize", SetJson());
        var discountBeforeApply = await Commerce.GetDiscountAsync(discount);
        Assert.Multiple(() =>
        {
            Assert.That(summary.GetProperty("entities").GetArrayLength(), Is.EqualTo(2));
            Assert.That(summary.GetProperty("applyPlan").EnumerateArray().Select(s => s.GetProperty("name").GetString()), Is.EqualTo(new[] { "Content", "Commerce" }));
            Assert.That(ContentService.GetById(page), Is.Null, "the page isn't saved before apply");
            Assert.That(discountBeforeApply, Is.Null, "the discount isn't saved before apply");
        });

        await _agent.OkAsync("changeSet_apply", SetJson());

        var savedPage = ContentService.GetById(page);
        var savedDiscount = await Commerce.GetDiscountAsync(discount);
        Assert.Multiple(() =>
        {
            Assert.That(savedPage?.GetValue<string>("title"), Is.EqualTo("10% off with SUMMER10"));
            Assert.That(savedDiscount?.Name, Is.EqualTo("Summer hats"));
            Assert.That(savedDiscount?.Codes.Single().Code, Is.EqualTo("SUMMER10"));
        });
    }

    [Test]
    public async Task A_discount_Commerce_refuses_stops_the_whole_set_so_the_page_beside_it_is_not_saved_either()
    {
        await Commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            var taken = await CommerceDiscount.CreateAsync(uow, _store.Id, "spring", "Spring");
            await taken.SetTypeAsync(DiscountType.Code);
            await taken.AddCodeAsync("SUMMER10", null, true);
            await taken.SetActiveAsync(true);
            await Commerce.SaveDiscountAsync(taken, ct);
            uow.Complete();
        });

        var page = Guid.NewGuid();
        var discount = Guid.NewGuid();
        await _agent.StartChangeSetAsync("Summer launch");
        await _agent.OkAsync("entity_create", $$"""
            { "entityType": "document", "key": "{{page}}", "subtype": "landingPage", "fields": { "name": "Summer hats", "title": "10% off with SUMMER10" } }
            """);
        await CreateSummerHatsAsync(discount);

        var refused = await _agent.FailAsync("changeSet_apply", SetJson());

        var savedDiscount = await Commerce.GetDiscountAsync(discount);
        Assert.Multiple(() =>
        {
            Assert.That(refused.GetProperty("status").GetString(), Is.EqualTo("InvalidInput"), refused.ToString());
            Assert.That(ContentService.GetById(page), Is.Null, "nothing in the set is applied");
            Assert.That(savedDiscount, Is.Null);
        });
    }

    [Test]
    public async Task The_rules_and_rewards_offered_are_Commerces_own_providers()
    {
        var schema = await _agent.OkAsync("entity_getSchema", $$"""{ "entityType": "{{Discount}}" }""");
        var fields = schema.GetProperty("fields").GetProperty("properties");

        var rules = ItemTypes(fields, "rules");
        var rewards = ItemTypes(fields, "rewards");
        Assert.Multiple(() =>
        {
            Assert.That(rules, Does.Contain("orderAmountDiscountRule"));
            Assert.That(rules, Does.Contain("groupDiscountRule"), "a group of rules, with its own match type");
            Assert.That(rewards, Does.Contain("orderAmountDiscountReward"));
        });
    }

    [Test]
    public async Task Each_rule_and_reward_setting_names_Commerces_own_editor_for_a_UI_and_not_for_the_model()
    {
        var top = (await _agent.OkAsync("entity_getSchema", $$"""{ "entityType": "{{Discount}}" }""")).GetProperty("fields").GetProperty("properties");
        var itemTypes = JsonSerializer.Serialize(ItemTypes(top, "rules").Concat(ItemTypes(top, "rewards")));
        var arguments = $$"""{ "entityType": "{{Discount}}", "itemTypes": {{itemTypes}} }""";

        // Commerce's provider settings name their editor UI. A form built from the schema shows the same editors.
        var ui = await _agent.UiOkAsync("entity_getSchema", arguments);
        var editors = ui.GetProperty("itemTypes").EnumerateObject()
            .SelectMany(t => t.Value.GetProperty("fields").GetProperty("properties").EnumerateObject())
            .Where(f => f.Value.TryGetProperty("x-editor", out _))
            .Select(f => f.Value.GetProperty("x-editor").GetProperty("alias").GetString())
            .Distinct()
            .ToList();
        Assert.That(editors, Is.Not.Empty);
        Assert.That(editors, Has.Some.StartsWith("Uc."), string.Join(", ", editors));

        var model = await _agent.OkAsync("entity_getSchema", arguments);
        Assert.That(model.ToString(), Does.Not.Contain("x-editor"));
    }

    [Test]
    public async Task An_amount_is_keyed_by_a_currency_the_model_finds_and_reads_back_as_an_object()
    {
        var key = Guid.NewGuid();
        await _agent.StartChangeSetAsync("Summer");
        var currency = await CreateSummerHatsAsync(key);

        var working = await _agent.OkAsync("entity_get", $$"""{ "entityType": "{{Discount}}", "key": "{{key}}" }""");
        var amounts = working.GetProperty("fields").GetProperty("rules")[0].GetProperty("amounts");

        Assert.That(amounts.ValueKind, Is.EqualTo(JsonValueKind.Object), amounts.ToString());
        Assert.That(amounts.GetProperty(currency.ToString()).GetDecimal(), Is.EqualTo(50m));
    }

    [Test]
    public async Task A_code_another_discount_already_uses_is_a_problem_from_Commerce_before_the_set_is_applied()
    {
        await Commerce.Uow.ExecuteAsync(async (uow, ct) =>
        {
            var taken = await CommerceDiscount.CreateAsync(uow, _store.Id, "spring", "Spring");
            await taken.SetTypeAsync(DiscountType.Code);
            await taken.AddCodeAsync("SUMMER10", null, true);
            await taken.SetActiveAsync(true);
            await Commerce.SaveDiscountAsync(taken, ct);
            uow.Complete();
        });

        var key = Guid.NewGuid();
        await _agent.StartChangeSetAsync("Summer");
        await CreateSummerHatsAsync(key);

        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        var messages = validation.GetProperty("entities")[0].GetProperty("problems").EnumerateArray()
            .SelectMany(p => p.GetProperty("errorMessages").EnumerateArray()).Select(m => m.GetString()).ToList();

        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.False);
        Assert.That(messages, Has.Some.StartsWith("Commerce: "), string.Join(", ", messages));
        Assert.That(await Commerce.GetDiscountAsync(key), Is.Null, "the check's save was rolled back");
    }

    [Test]
    public async Task A_user_without_the_Commerce_section_cannot_make_a_discount()
    {
        _agent.UserKey = await CreateWriterAsync();
        await _agent.StartChangeSetAsync("Summer");

        var refused = await _agent.FailAsync("entity_create", $$"""
            { "entityType": "{{Discount}}", "key": "{{Guid.NewGuid()}}", "fields": { "name": "Summer hats", "store": "{{_store.Id}}" } }
            """);

        Assert.That(refused.GetProperty("status").GetString(), Is.EqualTo("AccessDenied"));
        Assert.That(refused.GetProperty("message").GetString(), Does.Contain("Commerce section"));
    }

    // Builds "Summer hats" the way a model would: find the store and its currency, then create the discount with a
    // rule and a reward. Returns the currency's key.
    private async Task<Guid> CreateSummerHatsAsync(Guid key)
    {
        var store = (await _agent.OkAsync("entity_find", $$"""{ "entityType": "{{CommerceStoreAdapter.Type}}", "query": "shop" }"""))[0].GetProperty("key").GetGuid();
        var currency = (await _agent.OkAsync("entity_find", $$"""{ "entityType": "{{CommerceCurrencyAdapter.Type}}", "query": "shop" }"""))[0].GetProperty("key").GetGuid();

        await _agent.OkAsync("entity_create", $$"""
            {
              "entityType": "{{Discount}}", "key": "{{key}}",
              "fields": {
                "name": "Summer hats", "store": "{{store}}", "type": "code", "codes": ["SUMMER10"],
                "startDate": "2027-06-01", "endDate": "2027-08-31", "active": true
              }
            }
            """);
        await _agent.OkAsync("entity_addItem", $$"""
            {
              "entityType": "{{Discount}}", "key": "{{key}}", "fieldPath": "rules", "itemType": "orderAmountDiscountRule",
              "fields": { "priceType": "totalPrice", "comparisonOperator": "greaterThanOrEqual", "amounts": { "{{currency}}": 50 } }
            }
            """);
        await _agent.OkAsync("entity_addItem", $$"""
            {
              "entityType": "{{Discount}}", "key": "{{key}}", "fieldPath": "rewards", "itemType": "orderAmountDiscountReward",
              "fields": { "priceType": "subtotalPrice", "adjustmentType": "percentage", "percentage": 10 }
            }
            """);
        return currency;
    }

    private async Task<Guid> CreateWriterAsync()
    {
        var group = new UserGroup(GetRequiredService<IShortStringHelper>()) { Alias = "writers2", Name = "Writers 2", StartContentId = Constants.System.Root, HasAccessToAllLanguages = true };
        group.AddAllowedSection(Constants.Applications.Content);
        await GetRequiredService<IUserGroupService>().CreateAsync(group, Constants.Security.SuperUserKey);
        var created = await GetRequiredService<IUserService>().CreateAsync(
            Constants.Security.SuperUserKey,
            new UserCreateModel { Email = "writer@example.com", UserName = "writer@example.com", Name = "Writer", UserGroupKeys = new HashSet<Guid> { group.Key } },
            approveUser: true);
        Assert.That(created.Success, Is.True, created.Status.ToString());
        return created.Result.CreatedUser!.Key;
    }

    private static List<string?> ItemTypes(JsonElement fields, string field)
        => fields.GetProperty(field).GetProperty("x-item-types").EnumerateArray().Select(t => t.GetProperty("alias").GetString()).ToList();

    private string SetJson() => $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""";

    // Set once Umbraco has raised its starting notification, after every other handler (this one is added last).
    private sealed class StartedSignal
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed class Handler(StartedSignal signal) : INotificationHandler<UmbracoApplicationStartingNotification>
        {
            public void Handle(UmbracoApplicationStartingNotification notification) => signal.Started.TrySetResult();
        }
    }

    private sealed class PointCommerceAtThisTest : IHostedService
    {
        public PointCommerceAtThisTest(Umbraco.Commerce.Common.IFactory factory)
            => new Umbraco.Commerce.Cms.UmbracoCommerceComponent(factory).InitializeAsync(false, CancellationToken.None).GetAwaiter().GetResult();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A scripted model: calls the AI tools with JSON, as the CMS tests' agent does.</summary>
    private sealed class Agent(AbilityRegistry abilities, AbilityInvoker invoker)
    {
        private readonly AbilityToolSession _session = new(Constants.Security.SuperUserKey);
        private Dictionary<string, AIFunction>? _tools;

        private Dictionary<string, AIFunction> Tools => _tools ??= AbilityAITools.Create(abilities, invoker, _session).ToDictionary(t => t.Name);

        public Guid? ChangeSetId => _session.ChangeSetId;

        public Guid UserKey
        {
            get => _session.UserKey;
            set => _session.UserKey = value;
        }

        public async Task StartChangeSetAsync(string name) => await OkAsync("changeSet_create", $$"""{ "name": "{{name}}" }""");

        /// <summary>Calls an ability as a UI would, through the Management API: x-editor and x-meta are kept.</summary>
        public async Task<JsonElement> UiOkAsync(string tool, string json)
        {
            var ability = abilities.All.Single(a => AbilityTool.ToToolName(a.Descriptor.Id) == tool);
            var result = (await new AbilityTool(ability, invoker, _session).InvokeAsync(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject(), AbilityConsent.Given)).ToJson();
            Assert.That(result.GetProperty("success").GetBoolean(), Is.True, $"{tool} failed: {result}");
            return result.GetProperty("result");
        }

        public async Task<JsonElement> OkAsync(string tool, string json)
        {
            var result = await CallAsync(tool, json);
            Assert.That(result.GetProperty("success").GetBoolean(), Is.True, $"{tool} failed: {result}");
            return result.GetProperty("result");
        }

        public async Task<JsonElement> FailAsync(string tool, string json)
        {
            var result = await CallAsync(tool, json);
            Assert.That(result.GetProperty("success").GetBoolean(), Is.False, $"{tool} should have failed: {result}");
            return result;
        }

        private async Task<JsonElement> CallAsync(string tool, string json)
        {
            using var document = JsonDocument.Parse(json);
            var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
            return (JsonElement)(await Tools[tool].InvokeAsync(arguments))!;
        }
    }
}
