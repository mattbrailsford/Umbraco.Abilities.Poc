using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// A model always sees a plugin's settings in the framework's JSON. How they are stored is the product's choice: .NET's
/// own form by default, or with the JSON options the product already serializes with.
/// </summary>
public class PluginSettingFormatTests
{
    private enum PriceType
    {
        SubtotalPrice,
        TotalPrice,
    }

    private static readonly SettingDescriptor[] Settings =
    [
        new("priceType", null, null, typeof(PriceType), []),
        new("amounts", null, null, typeof(IDictionary<Guid, decimal?>), []),
        new("note", null, null, typeof(string), []),
    ];

    private static readonly Guid Currency = Guid.NewGuid();

    [Fact]
    public void By_default_a_setting_is_stored_in_its_types_own_dotnet_form()
    {
        var stored = PluginSettings.ToSettings(Item(), Plugin());

        Assert.Equal("TotalPrice", stored["priceType"]);
        Assert.Equal($$"""{"{{Currency}}":50}""", stored["amounts"]);
        Assert.Equal("Hats only", stored["note"]);
    }

    [Fact]
    public void A_product_that_supplies_its_json_options_has_its_settings_stored_with_them_and_the_model_still_sees_the_same_json()
    {
        // The options the product already serializes with: here, enums in upper snake case.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) } };
        var plugin = Plugin() with { SettingJsonOptions = options };

        var stored = PluginSettings.ToSettings(Item(), plugin);
        Assert.Equal("TOTAL_PRICE", stored["priceType"]);

        var read = PluginSettings.ToItem(Guid.NewGuid(), "orderAmount", stored, plugin);
        Assert.Equal("totalPrice", read.Fields["priceType"]!.GetValue<string>());
        Assert.Equal(50m, read.Fields["amounts"]![Currency.ToString()]!.GetValue<decimal>());
    }

    [Fact]
    public void A_stored_value_the_type_cannot_read_is_shown_as_it_was_stored()
    {
        var read = PluginSettings.ToItem(Guid.NewGuid(), "orderAmount", new Dictionary<string, string> { ["priceType"] = "NotAPriceType" }, Plugin());

        Assert.Equal("NotAPriceType", read.Fields["priceType"]!.GetValue<string>());
    }

    private static PluginDescriptor Plugin() => new("orderAmount", "Order amount", null, Settings);

    // What a model sends: camel-case enum names, and an amount it gave as text.
    private static DynamicItem Item() => new()
    {
        Key = Guid.NewGuid(),
        Type = "orderAmount",
        Fields =
        {
            ["priceType"] = "totalPrice",
            ["amounts"] = new JsonObject { [Currency.ToString()] = "50" },
            ["note"] = "Hats only",
        },
    };
}
