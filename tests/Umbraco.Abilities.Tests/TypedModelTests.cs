using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;
using Umbraco.Abilities.Samples;
using Umbraco.Abilities.Samples.Cms;
using Umbraco.Abilities.Samples.Commerce;
using Umbraco.Abilities.Samples.Commerce.Domain;

using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
namespace Umbraco.Abilities.Tests;

/// <summary>What the framework works out from a package's model classes, so the package doesn't write it.</summary>
public class TypedModelTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly ScriptedAgent _agent;

    public TypedModelTests() => _agent = new ScriptedAgent(_site);

    [Fact]
    public async Task The_structure_comes_from_the_model_class_and_its_attributes()
    {
        var types = await TypesAsync(_site.Discounts);
        var discount = await _site.Discounts.GetSchemaAsync(null);
        var fields = discount.Root.Fields.ToDictionary(f => f.Alias);

        Assert.True(fields["name"].Required);
        Assert.Equal(FieldType.Date, fields["startDate"].Type);
        Assert.Equal("document", fields["landingPage"].ReferencesEntityType);
        Assert.Equal(1, discount.Root.Fields.Single(f => f.Alias == "rules").Min);
        Assert.Equal(3, discount.Root.Fields.Single(f => f.Alias == "rewards").Max);
        Assert.DoesNotContain("key", fields.Keys);
        Assert.DoesNotContain("version", fields.Keys);

        var customerGroup = types.Find(DiscountAdapter.RuleGroup, "customerGroup")!;
        Assert.Equal(["VIP", "Wholesale", "Retail"], customerGroup.Schema.Fields.Single().Allowed);
        Assert.Null(customerGroup.ClrType); // from Commerce's provider collection, not a class
    }

    [Fact]
    public async Task Varying_values_settings_layout_and_json_values_are_described_too()
    {
        var types = await TypesAsync(_site.Pages);
        var page = await _site.Pages.GetSchemaAsync(null);
        var fields = page.Root.Fields.ToDictionary(f => f.Alias);

        Assert.Equal(["en-US", "da-DK"], Assert.Single(page.Dimensions).Values);
        Assert.Equal(["culture"], fields["title"].VariesBy);
        Assert.True(fields["tags"].Many);
        Assert.Equal(["document", "media"], fields["related"].ReferencesAnyOf);
        Assert.Equal(["culture"], page.Root.Fields.Single(f => f.Alias == "promos").VariesBy);
        Assert.Equal("columnSpan", types.Find(fields["grid"].ItemTypeGroup!, "richText")!.Schema.Find("layout")!.Schema!.Fields[0].Alias);

        var hero = types.Find(fields["blocks"].ItemTypeGroup!, "hero")!;
        Assert.Equal(["light", "dark"], hero.Schema.Find("settings")!.Schema!.Find("theme")!.Allowed);

        var focalPoint = types.Find(fields["image"].ItemTypeGroup!, "mediaEntry")!.Schema.Fields.Single(f => f.Alias == "focalPoint");
        Assert.Equal(FieldType.Json, focalPoint.Type);
        Assert.NotNull(focalPoint.JsonSchema!["properties"]!["left"]);
    }

    [Fact]
    public async Task A_model_round_trips_through_its_document_with_item_keys_and_paths()
    {
        var page = new Page
        {
            Key = Guid.NewGuid(),
            Title = { ["en-US"] = "Launch", ["da-DK"] = "Lancering" },
            Blocks = [new Hero { Heading = { ["en-US"] = "Hello" }, Settings = new HeroSettings { Theme = "dark" } }],
            Grid = [new RichText { Text = { ["en-US"] = "Body" }, Layout = new GridPlacement { ColumnSpan = 6 } }],
            Promos = { ["da-DK"] = [new Promo { Text = "Udsalg" }] },
            Image = [new MediaEntry { Media = Guid.NewGuid(), FocalPoint = new FocalPoint(0.5, 0.25) }],
        };

        var entity = _site.Pages.ToEntity(page);
        var copy = _site.Pages.ToModel(entity);

        Assert.Equal("Lancering", copy.Title["da-DK"]);
        var hero = Assert.IsType<Hero>(Assert.Single(copy.Blocks));
        Assert.Equal(page.Blocks[0].Key, hero.Key); // a key was assigned on the way in and kept
        Assert.Equal($"blocks/{hero.Key}", hero.Path);
        Assert.Equal("dark", hero.Settings!.Theme);
        Assert.Equal(6, copy.Grid[0].Layout!.ColumnSpan);
        Assert.Equal("Udsalg", copy.Promos["da-DK"][0].Text);
        Assert.Equal(new FocalPoint(0.5, 0.25), copy.Image[0].FocalPoint);
    }

    [Fact]
    public async Task Each_problem_is_reported_once_whether_it_comes_from_the_structure_or_an_attribute()
    {
        var discount = new DiscountEditModel
        {
            Key = Guid.NewGuid(),
            Rules = [new DynamicItem { Type = "orderTotal" }],
            Rewards = [new DynamicItem { Type = "percentageOff", Fields = { ["percent"] = 150 } }],
            StartDate = new DateOnly(2026, 12, 31),
            EndDate = new DateOnly(2026, 12, 1),
        };
        var entity = _site.Discounts.ToEntity(discount);
        var schema = await _site.Discounts.GetSchemaAsync(null);

        IReadOnlyList<Problem> problems =
        [
            .. await SchemaValidator.ValidateAsync(entity, schema, await TypesAsync(_site.Discounts), (_, _) => Task.FromResult(true)),
            .. await _site.Discounts.ValidateAsync(entity),
        ];

        Assert.Equal(
            ["endDate", "name", $"rewards/{discount.Rewards[0].Key}/percent", $"rules/{discount.Rules[0].Key}/minTotal"],
            problems.Select(p => p.Path).Order());
    }

    [Fact]
    public async Task Item_types_come_from_the_lists_element_type_and_can_be_narrowed_by_naming_classes()
    {
        var types = await TypesAsync(_site.Pages);
        var page = await _site.Pages.GetSchemaAsync(null);
        var fields = page.Root.Fields.ToDictionary(f => f.Alias);
        string[] Aliases(string group) => types.InGroup(group).Select(t => t.Alias).Order().ToArray();

        // Nothing was registered by hand: every [ItemType] class that extends the list's element type is found.
        Assert.Equal(["cardGrid", "hero"], Aliases(fields["blocks"].ItemTypeGroup!));
        Assert.Equal(["richText", "twoColumn"], Aliases(fields["grid"].ItemTypeGroup!));

        // Item types inside item types are found too, and a list can be narrowed to the classes it names.
        var twoColumn = types.Find(fields["grid"].ItemTypeGroup!, "twoColumn")!;
        Assert.Equal(["richText"], Aliases(twoColumn.Schema.Find("left")!.ItemTypeGroup!));
        Assert.Equal(["card"], Aliases(types.Find(fields["blocks"].ItemTypeGroup!, "cardGrid")!.Schema.Find("cards")!.ItemTypeGroup!));
    }

    [Fact]
    public async Task A_class_with_no_attributes_or_base_class_is_described_fluently()
    {
        var types = await TypesAsync(_site.Media);
        var media = await _site.Media.GetSchemaAsync(null);
        var fields = media.Root.Fields.ToDictionary(f => f.Alias);

        Assert.Equal("media", media.EntityType);
        Assert.True(fields["name"].Required);
        Assert.Equal(["name", "altText", "crops"], fields.Keys); // the key, version and server-side UpdateDate are not fields
        var crops = Assert.Single(media.Root.ItemFields);

        var crop = types.Find(crops.ItemTypeGroup!, "crop")!;
        Assert.Equal(typeof(MediaCropRequestModel), crop.ClrType);
        Assert.All(crop.Schema.Fields, f => Assert.True(f.Required));

        // Items keep their own key property and get a path without deriving from EntityItemModel.
        var request = new MediaRequestModel { Id = Guid.NewGuid(), Name = "Hero", Crops = [new MediaCropRequestModel { Alias = "wide", Width = 0, Height = 600 }] };
        var copy = _site.Media.ToModel(_site.Media.ToEntity(request));
        var key = request.Crops[0].Id;
        Assert.NotEqual(Guid.Empty, key);
        Assert.Equal(key, copy.Crops[0].Id);
        Assert.Equal($"crops/{key}", ModelSchemas.PathOf(copy.Crops[0]));

        // A fluent rule is reported on the item's path, like an attribute.
        var problems = await _site.Media.ValidateAsync(_site.Media.ToEntity(copy));
        Assert.Equal([$"crops/{key}/width"], problems.Select(p => p.Path));
    }

    [Fact]
    public async Task A_fluently_described_entity_is_created_from_text_like_any_other()
    {
        await _agent.StartChangeSetAsync("Media");
        var media = (await _agent.OkAsync("entity_create", """{ "entityType": "media", "fields": { "name": "Hero image" } }""")).GetProperty("key").GetGuid();
        await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "media", "key": "{{media}}", "fieldPath": "crops", "itemType": "crop", "fields": { "alias": "wide", "width": 1600, "height": 600 } }
            """);
        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");

        var live = (await _site.Media.LoadModelAsync(media))!;
        Assert.Equal("Hero image", live.Name);
        Assert.Equal(1600, Assert.Single(live.Crops).Width);
    }

    [Fact]
    public async Task Commerce_keeps_its_own_model_and_the_definition_maps_to_it()
    {
        await _agent.StartChangeSetAsync("Sale");
        var discount = (await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-commerce-discount", "fields": { "name": "Sale", "endDate": "2026-12-31" } }""")).GetProperty("key").GetGuid();
        var rule = (await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "umbraco-commerce-discount", "key": "{{discount}}", "fieldPath": "rules", "itemType": "orderTotal", "fields": { "minTotal": 50 } }""")).GetProperty("itemKey").GetGuid();
        await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "umbraco-commerce-discount", "key": "{{discount}}", "fieldPath": "rewards", "itemType": "freeShipping" }""");
        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");

        // Stored in Commerce's own shape: a provider alias and string settings, UTC dates.
        var stored = _site.DiscountService.GetDiscount(discount)!;
        Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), stored.ExpiryDate);
        Assert.Equal("orderTotal", stored.Rules[0].RuleProviderAlias);
        Assert.Equal("50", stored.Rules[0].Settings["minTotal"]);

        // Commerce's rules have no ids, so keys are derived: stable across loads, but not the key the agent was given.
        var first = (await _site.Discounts.LoadModelAsync(discount))!.Rules[0].Key;
        var second = (await _site.Discounts.LoadModelAsync(discount))!.Rules[0].Key;
        Assert.Equal(first, second);
        Assert.NotEqual(rule, first);
    }

    [Fact]
    public async Task A_new_entity_starts_with_its_models_default_values()
    {
        // [DefaultValue(false)] on the discount's Active: in the schema, and filled in when it's created without one.
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "umbraco-commerce-discount" }""");
        Assert.False(schema.GetProperty("fields").GetProperty("properties").GetProperty("active").GetProperty("default").GetBoolean());

        await _agent.StartChangeSetAsync("Sale");
        var discount = (await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-commerce-discount", "fields": { "name": "Sale" } }""")).GetProperty("key").GetGuid();
        var entity = await _agent.OkAsync("entity_get", $$"""{ "entityType": "umbraco-commerce-discount", "key": "{{discount}}" }""");
        Assert.False(entity.GetProperty("fields").GetProperty("active").GetBoolean(), entity.ToString());
    }

    [Fact]
    public void An_edit_ability_finds_its_key_from_the_inputs_entity_key_property()
    {
        // ActivateDiscountAbility writes no KeyOf: its input's [EntityKey("umbraco-commerce-discount")] property is the key.
        var discount = Guid.NewGuid();
        var target = new ActivateDiscountAbility(_site.Discounts).Prepare(new System.Text.Json.Nodes.JsonObject { ["discount"] = discount.ToString() });
        Assert.Equal(discount, target.EntityKey);

        // An input without one says what to add.
        var error = Assert.Throws<InvalidOperationException>(() => new UnmarkedEditAbility(_site.Discounts).Prepare(new System.Text.Json.Nodes.JsonObject { ["discount"] = discount.ToString() }));
        Assert.Contains("[EntityKey(\"umbraco-commerce-discount\")]", error.Message);
    }

[Fact]
    public async Task A_fields_allowed_item_types_can_come_from_a_setting_read_at_run_time()
    {
        // The page's blocks field allows what its data type's configuration lists, as a Block List does.
        var page = await _site.Pages.GetSchemaAsync(null);
        var blocks = page.Root.Fields.Single(f => f.Alias == "blocks").ItemTypeGroup!;
        Assert.Equal(["cardGrid", "hero"], (await TypesAsync(_site.Pages)).InGroup(blocks).Select(t => t.Alias).Order());

        var saved = new Page { Key = Guid.NewGuid(), Blocks = [new CardGrid()] };
        await _site.Pages.SaveModelAsync(saved);

        // An editor removes the card grid from the data type: it is no longer offered, and can't be added.
        _site.Pages.AllowedBlocks.Remove(typeof(CardGrid));
        Assert.Equal(["hero"], (await TypesAsync(_site.Pages)).InGroup(blocks).Select(t => t.Alias));

        await _agent.StartChangeSetAsync("Page");
        var key = (await _agent.OkAsync("entity_create", """{ "entityType": "document" }""")).GetProperty("key").GetGuid();
        await _agent.FailAsync("entity_addItem", $$"""{ "entityType": "document", "key": "{{key}}", "fieldPath": "blocks", "itemType": "cardGrid" }""");

        // A page saved with a card grid before the change still loads it, and validation reports it.
        Assert.IsType<CardGrid>(Assert.Single((await _site.Pages.LoadModelAsync(saved.Key))!.Blocks));
        var problems = await SchemaValidator.ValidateAsync((await _site.Pages.LoadAsync(saved.Key))!, page, await TypesAsync(_site.Pages), (_, _) => Task.FromResult(true));
        Assert.Contains(problems, p => p.Path == $"blocks/{saved.Blocks[0].Key}");
    }

    private sealed record UnmarkedInput(Guid Discount);

    [Ability("test.unmarked", "Unmarked", Kind = AbilityKind.Write)]
    private sealed class UnmarkedEditAbility(DiscountAdapter discounts) : ModelEditAbilityBase<UnmarkedInput, DiscountEditModel>(discounts)
    {
        protected override void Edit(DiscountEditModel discount, UnmarkedInput input) => discount.Active = true;
    }

    // The item types an adapter's entities can hold, read as a call would read them.
    private async Task<ItemTypes> TypesAsync(IEntityAdapter adapter)
        => await _site.ItemTypes.ResolveAsync((await adapter.GetSchemaAsync(null)).Root, null);
}
