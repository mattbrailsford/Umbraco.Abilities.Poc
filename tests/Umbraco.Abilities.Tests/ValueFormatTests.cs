using System.Text.Json;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// A value type names its kind once, with <c>[ValueFormat]</c>, and every field of that type gets JSON Schema's
/// <c>format</c>: in any ability's input, entity or plugin setting. A UI maps the format to one editor. A model sees the
/// format too, since it says what the value is.
/// </summary>
public class ValueFormatTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly ScriptedAgent _agent;

    public ValueFormatTests() => _agent = new ScriptedAgent(_site);

    private const string StepSchemas = """{ "entityType": "umbraco-automate-workflow", "itemTypes": ["schedule", "umbracoAutomate.if"] }""";

    [Fact]
    public async Task A_value_type_names_its_format_once_and_every_setting_of_that_type_has_it()
    {
        // Automate's schedule trigger has a CronExpression setting. Nothing on the setting says cron: the type does.
        var cron = Fields(await _agent.OkAsync("entity_getSchema", StepSchemas), "schedule").GetProperty("cron");

        Assert.Equal("string", cron.GetProperty("type").GetString());
        Assert.Equal("umbraco.cron", cron.GetProperty("format").GetString());
    }

    [Fact]
    public void An_input_property_can_name_a_standard_format_and_a_model_sees_it()
    {
        var to = _agent.Tools["email_send"].JsonSchema.GetProperty("properties").GetProperty("to");

        Assert.Equal("email", to.GetProperty("format").GetString());
    }

    [Fact]
    public void The_fluent_set_up_names_a_format_too()
    {
        Entities.Models.ModelConfiguration.Configure<Job>(job => job.Field(j => j.When).Format("umbraco.cron"));

        Assert.Equal("umbraco.cron", Entities.Models.ModelSchemas.SchemaOf(typeof(Job)).Find("when")!.Format);
    }

    [Theory]
    [InlineData("cron")]
    [InlineData("Umbraco.Cron")]
    [InlineData("umbraco.")]
    [InlineData("date time")]
    public void A_format_is_one_of_JSON_Schemas_own_or_owner_dot_name_in_lower_case(string format)
        => Assert.Throws<ArgumentException>(() => ValueFormats.Check(format));

    [Theory]
    [InlineData("email")]
    [InlineData("date-time")]
    [InlineData("umbraco.cron")]
    [InlineData("acme.colour-hex")]
    public void A_standard_format_or_a_packages_own_is_accepted(string format)
        => Assert.Equal(format, ValueFormats.Check(format));

    [Fact]
    public async Task A_products_own_editor_for_a_setting_becomes_the_fields_editor()
    {
        // Automate's If names its condition builder. A form shows that, as Automate's own editor does. The model doesn't
        // see it: it reads the description and the schema of the conditions instead.
        var ui = Fields(await _agent.UiOkAsync("entity_getSchema", StepSchemas), "umbracoAutomate.if").GetProperty("conditions");
        Assert.Equal("UmbracoAutomate.PropertyEditorUi.ConditionBuilder", ui.GetProperty("x-editor").GetProperty("alias").GetString());

        var model = Fields(await _agent.OkAsync("entity_getSchema", StepSchemas), "umbracoAutomate.if").GetProperty("conditions");
        Assert.False(model.TryGetProperty("x-editor", out _));
    }

    [Fact]
    public async Task A_form_maps_a_format_to_one_editor_for_every_field_of_that_kind()
    {
        // The UI registers a cron editor for the format once. The schedule trigger gets it with nothing on the setting.
        var form = new FormEditors(registered: [], formats: new() { ["umbraco.cron"] = "Test.CronEditor", ["email"] = "Test.EmailEditor" });

        var cron = Fields(await _agent.UiOkAsync("entity_getSchema", StepSchemas), "schedule").GetProperty("cron");
        Assert.Equal("Test.CronEditor", form.EditorFor(cron));
        Assert.Equal("Test.EmailEditor", form.EditorFor(_agent.UiTool("email_send").JsonSchema.GetProperty("properties").GetProperty("to")));

        // A UI without the cron editor still shows a text box: an unknown format is only a hint.
        Assert.Equal("Umb.PropertyEditorUi.TextBox", new FormEditors([], []).EditorFor(cron));
    }

    private static JsonElement Fields(JsonElement schema, string itemType)
        => schema.GetProperty("itemTypes").GetProperty(itemType).GetProperty("fields").GetProperty("properties");

    public sealed class Job
    {
        public string? When { get; set; }
    }
}
