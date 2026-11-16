using System.Text.Json.Nodes;

using Umbraco.Abilities.Entities;

namespace Umbraco.Abilities.Tests;

/// <summary>The value tree and the checks on it that need no product.</summary>
public class EntityTests
{
    private static readonly ItemTypes ItemTypes = ItemTypes.Of(
        new ItemTypeDefinition("blocks", "hero", "Hero", "A hero.", ObjectSchema.Of(
            new FieldDefinition("heading", "Heading.", FieldType.String) { VariesBy = ["culture"] },
            FieldDefinition.ObjectOf("settings", "Settings.", ObjectSchema.Of(new FieldDefinition("theme", "Theme.", FieldType.String))))));

    private static readonly ObjectSchema Page = ObjectSchema.Of(
        FieldDefinition.ItemsOf("blocks", "Blocks.", "blocks"),
        new FieldDefinition("data", "Any JSON.", FieldType.Json));

    [Fact]
    public void A_path_goes_through_items_by_key_and_objects_by_field_in_the_variant_each_value_varies_by()
    {
        var key = Guid.NewGuid();
        var root = JsonNode.Parse($$"""
            { "blocks": [ { "key": "{{key}}", "type": "hero", "heading": { "en-US": "Hi", "da-DK": "Hej" }, "settings": { "theme": "dark" } } ] }
            """)!.AsObject();

        PathTarget Find(string path) => EntityValues.Find(root, Page, ItemTypes, path, (field, _) => VariantId.Of("culture", "da-DK").Narrow(field.VariesBy), create: false);

        Assert.Equal("Hej", Find($"blocks/{key}/heading").Value!.GetValue<string>());
        Assert.Equal("dark", Find($"blocks/{key}/settings/theme").Value!.GetValue<string>());
        Assert.Equal(key, Find($"blocks/{key}").ItemKey);
        Assert.Throws<AbilityInputException>(() => Find("blocks/0/heading")); // never by position
    }

    [Fact]
    public void Variation_is_any_dimension_a_product_names_not_only_culture()
    {
        // A price per currency inside a reward item: the schema lists the dimension and its values.
        var itemTypes = ItemTypes.Of(new ItemTypeDefinition("rewards", "amountOff", "Amount off", "A fixed amount off.", ObjectSchema.Of(
            new FieldDefinition("amount", "The amount, per currency.", FieldType.Number, Required: true) { VariesBy = ["currency"] })));
        var schema = new EntitySchema("discount", "Discount", "A discount.", ObjectSchema.Of(FieldDefinition.ItemsOf("rewards", "Rewards.", "rewards")))
        {
            Dimensions = [new VariationDimension("currency", "The store's currencies.", ["EUR", "DKK"])],
        };
        var entity = new Entity { EntityType = "discount", Key = Guid.NewGuid() };
        EditTarget In(string? currency) => new(entity, schema, itemTypes, VariantId.Of("currency", currency));

        var reward = EntityEditor.AddItem(In(null), "rewards", "amountOff", Guid.NewGuid(), null, null);
        EntityEditor.SetValue(In("EUR"), $"{reward}/amount", JsonValue.Create(10));
        EntityEditor.SetValue(In("DKK"), $"{reward}/amount", JsonValue.Create(75));

        var item = entity.Fields["rewards"]![0]!;
        Assert.Equal(75, item["amount"]!["DKK"]!.GetValue<int>());

        var missing = Assert.Throws<AbilityInputException>(() => EntityEditor.SetValue(In(null), $"{reward}/amount", JsonValue.Create(5)));
        Assert.Equal("This varies by currency. Give a currency in the variant: EUR, DKK.", missing.Message);
        Assert.Throws<AbilityInputException>(() => EntityEditor.SetValue(In("USD"), $"{reward}/amount", JsonValue.Create(5)));
    }

    // A page whose heading varies by culture and segment at once, as a CMS property can. Cultures are listed; segments are open.
    private static readonly EntitySchema SegmentedPage = new("page", "Page", "A page.", ObjectSchema.Of(
        new FieldDefinition("heading", "Heading.", FieldType.String, Required: true) { VariesBy = ["culture", "segment"] },
        new FieldDefinition("subtitle", "Subtitle.", FieldType.String) { VariesBy = ["culture", "segment"] }))
    {
        Dimensions =
        [
            new VariationDimension("culture", "The site's languages.", ["en-US", "da-DK"]),
            new VariationDimension("segment", "A segment of the audience. Leave it out for the default."),
        ],
    };

    private static EditTarget In(Entity entity, string? culture, string? segment)
        => new(entity, SegmentedPage, ItemTypes.Of(), VariantId.Of("culture", culture).With("segment", segment));

    [Fact]
    public void A_value_can_vary_by_two_dimensions_and_each_segment_keeps_its_own_value()
    {
        var entity = new Entity { EntityType = "page", Key = Guid.NewGuid() };

        EntityEditor.SetValue(In(entity, "en-US", null), "heading", JsonValue.Create("Welcome"));
        EntityEditor.SetValue(In(entity, "en-US", "vip"), "heading", JsonValue.Create("Welcome back, VIP"));
        EntityEditor.SetValue(In(entity, "en-US", "new"), "heading", JsonValue.Create("Welcome, new visitor"));
        EntityEditor.SetValue(In(entity, "da-DK", "vip"), "heading", JsonValue.Create("Velkommen tilbage, VIP"));

        // Stored under one key per variant: culture, then segment, in the field's order. The default segment adds nothing.
        var stored = entity.Fields["heading"]!.AsObject();
        Assert.Equal(["en-US", "en-US|vip", "en-US|new", "da-DK|vip"], stored.Select(v => v.Key));

        // Each segment reads back its own value, and the default segment is its own value, not a fallback.
        string? Read(string culture, string? segment) => EntityEditor.Find(In(entity, culture, segment), "heading", create: false).Value?.GetValue<string>();
        Assert.Equal("Welcome", Read("en-US", null));
        Assert.Equal("Welcome back, VIP", Read("en-US", "vip"));
        Assert.Equal("Welcome, new visitor", Read("en-US", "new"));
        Assert.Equal("Velkommen tilbage, VIP", Read("da-DK", "vip"));
        Assert.Null(Read("da-DK", null));
        Assert.Null(Read("da-DK", "new"));

        // Walking the values gives each one back with both dimensions.
        var variants = EntityValues.Values(entity.Fields, SegmentedPage.Root.Fields[0]).Select(v => v.Variant.Key);
        Assert.Equal(["culture=en-US", "culture=en-US;segment=vip", "culture=en-US;segment=new", "culture=da-DK;segment=vip"], variants);

        // Removing one segment's value leaves the others.
        EntityEditor.SetValue(In(entity, "en-US", "vip"), "heading", null);
        Assert.Null(Read("en-US", "vip"));
        Assert.Equal("Welcome, new visitor", Read("en-US", "new"));
    }

    [Fact]
    public void A_segment_still_needs_a_known_culture()
    {
        var entity = new Entity { EntityType = "page", Key = Guid.NewGuid() };

        var noCulture = Assert.Throws<AbilityInputException>(() => EntityEditor.SetValue(In(entity, null, "vip"), "heading", JsonValue.Create("Hi")));
        Assert.Equal("This varies by culture. Give a culture in the variant: en-US, da-DK.", noCulture.Message);
        Assert.Throws<AbilityInputException>(() => EntityEditor.SetValue(In(entity, "fr-FR", "vip"), "heading", JsonValue.Create("Salut")));
        Assert.Null(entity.Fields["heading"]);
    }

    [Fact]
    public async Task Problems_in_a_segment_say_which_culture_and_segment()
    {
        var entity = new Entity { EntityType = "page", Key = Guid.NewGuid() };
        EntityEditor.SetValue(In(entity, "en-US", null), "heading", JsonValue.Create("Welcome"));
        EntityEditor.SetValue(In(entity, "en-US", "vip"), "heading", JsonValue.Create("Welcome back, VIP"));

        // The Danish VIP variant exists (it has a subtitle) but has no heading.
        EntityEditor.SetValue(In(entity, "da-DK", "vip"), "subtitle", JsonValue.Create("Kun for VIP"));

        var problems = await SchemaValidator.ValidateAsync(entity, SegmentedPage, ItemTypes.Of(), (_, _) => Task.FromResult(true));

        var problem = Assert.Single(problems);
        Assert.Equal("heading", problem.Path);
        Assert.Equal(new Dictionary<string, string> { ["culture"] = "da-DK", ["segment"] = "vip" }, problem.Variant);
    }

    [Fact]
    public void A_JSON_value_shaped_like_items_is_a_value_because_the_structure_says_so()
    {
        var root = JsonNode.Parse($$"""{ "data": [ { "key": "{{Guid.NewGuid()}}", "type": "hero" } ] }""")!.AsObject();

        var values = EntityValues.Walk(root, Page, ItemTypes).ToList();

        Assert.Equal(["data"], values.Select(v => v.Path));
        Assert.Equal(FieldType.Json, values[0].Field.Type);
    }

    [Fact]
    public async Task An_item_type_is_checked_in_its_own_fields_group_when_its_alias_is_in_several()
    {
        var restricted = Guid.NewGuid();
        var itemTypes = await new ItemTypeRegistry()
            .Add(new ItemTypeDefinition("grid", "text", "Text", "Text in a grid.", ObjectSchema.Of()) { AvailableTo = (user, _) => ValueTask.FromResult(user != restricted) })
            .Add(new ItemTypeDefinition("list", "text", "Text", "Text in a list.", ObjectSchema.Of()))
            .ResolveAsync(restricted);
        var schema = ObjectSchema.Of(FieldDefinition.ItemsOf("blocks", "Blocks.", "list"), FieldDefinition.ItemsOf("grid", "Grid.", "grid"));

        var after = new Entity { EntityType = "page", Key = Guid.NewGuid() };
        after.Fields["blocks"] = new JsonArray(EntityValues.NewItem(Guid.NewGuid(), "text"));
        Assert.Empty(EntityAccess.NewItemTypesNotAvailable(null, after, schema, itemTypes));

        after.Fields["grid"] = new JsonArray(EntityValues.NewItem(Guid.NewGuid(), "text"));
        Assert.Equal(["text"], EntityAccess.NewItemTypesNotAvailable(null, after, schema, itemTypes));
    }

    [Fact]
    public void A_binding_is_written_with_a_dollar_and_braces_and_is_checked_when_it_resolves_not_when_set()
    {
        var quantity = new FieldDefinition("quantity", "How many.", FieldType.Integer)
        {
            AcceptsBindings = true,
            Rules = [new System.ComponentModel.DataAnnotations.RangeAttribute(1, 10)],
        };

        Assert.Null(FieldChecker.Check(quantity, JsonValue.Create("${trigger.quantity}"), "quantity"));
        Assert.Empty(FieldChecker.CheckRules(quantity, JsonValue.Create("${trigger.quantity}")));

        // Not Automate's binding syntax, so it's checked as a literal: a string where a whole number goes.
        Assert.NotNull(FieldChecker.Check(quantity, JsonValue.Create("{{trigger.quantity}}"), "quantity"));

        // A field that takes no bindings checks the same text as a literal.
        Assert.NotNull(FieldChecker.Check(quantity with { AcceptsBindings = false }, JsonValue.Create("${trigger.quantity}"), "quantity"));
    }

    [Fact]
    public async Task Sources_allow_lists_and_availability_can_wait_and_are_read_once_per_call()
    {
        var reads = 0;
        var registry = new ItemTypeRegistry()
            .AddSource(new ItemTypeSource("blocks", async cancellationToken =>
            {
                await Task.Delay(1, cancellationToken); // a database or service call
                reads++;
                return (IEnumerable<ItemTypeDefinition>)
                [
                    new ItemTypeDefinition("blocks", "hero", "Hero", "A hero.", ObjectSchema.Of()),
                    new ItemTypeDefinition("blocks", "quote", "Quote", "A quote.", ObjectSchema.Of())
                    {
                        AvailableTo = async (user, cancellationToken) => { await Task.Delay(1, cancellationToken); return false; },
                    },
                ];
            }))
            .Allow("blocks", async cancellationToken => { await Task.Delay(1, cancellationToken); return ["hero", "quote"]; });

        var itemTypes = await registry.ResolveAsync(Guid.NewGuid());

        Assert.Equal(["hero", "quote"], itemTypes.InGroup("blocks").Select(t => t.Alias));
        Assert.False(itemTypes.IsAvailable(itemTypes.Find("blocks", "quote")!));
        Assert.Equal(1, reads);
    }
}
