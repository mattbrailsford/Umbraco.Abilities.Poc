using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// Two schema keywords are for UIs, not models: <c>x-editor</c> names the editor a field uses, and <c>x-meta</c> is an
/// open bag of hints, each owned by whoever defines it. A form built from a schema (Automate's settings form, a
/// backoffice screen for a change set) reads them; the AI tools and MCP strip them.
/// </summary>
public class UiHintsTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly ScriptedAgent _agent;

    public UiHintsTests() => _agent = new ScriptedAgent(_site);

    private const string HeroSchema = """{ "entityType": "document", "itemTypes": ["hero"] }""";

    [Fact]
    public async Task A_field_can_name_its_own_editor_and_carry_hints_that_a_UI_sees_and_a_model_does_not()
    {
        // Acme's theme picker, set with [Editor] and [Meta] on the hero's settings: a field two levels down.
        var ui = Theme(await _agent.UiOkAsync("entity_getSchema", HeroSchema));
        Assert.Equal("Acme.ThemePicker", ui.GetProperty("x-editor").GetProperty("alias").GetString());
        Assert.True(ui.GetProperty("x-editor").GetProperty("config").GetProperty("swatches").GetBoolean());
        Assert.Equal("hero", ui.GetProperty("x-meta").GetProperty("acme.theme").GetProperty("preview").GetString());

        // The model gets the same field, with what it needs (the allowed values), and nothing else.
        var model = Theme(await _agent.OkAsync("entity_getSchema", HeroSchema));
        Assert.False(model.TryGetProperty("x-editor", out _));
        Assert.False(model.TryGetProperty("x-meta", out _));
        Assert.Equal(["light", "dark"], model.GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void An_abilitys_input_can_name_its_editor_for_a_form_built_from_its_schema()
    {
        // [Editor] on an input record's parameter: the body is a long text, which nothing in the plain shape says.
        var ui = _agent.UiTool("email_send").JsonSchema.GetProperty("properties").GetProperty("body");
        Assert.Equal("Umb.PropertyEditorUi.TextArea", ui.GetProperty("x-editor").GetProperty("alias").GetString());
        Assert.Equal(8, ui.GetProperty("x-editor").GetProperty("config").GetProperty("rows").GetInt32());

        var model = _agent.Tools["email_send"].JsonSchema.GetProperty("properties").GetProperty("body");
        Assert.False(model.TryGetProperty("x-editor", out _));
        Assert.Equal("Optional body text.", model.GetProperty("description").GetString());
    }

    [Fact]
    public void The_fluent_set_up_names_an_editor_and_adds_hints_too()
    {
        Entities.Models.ModelConfiguration.Configure<Ticket>(ticket => ticket
            .Field(t => t.Summary).Editor("Umb.PropertyEditorUi.TextArea").Meta("acme.support", new JsonObject { ["queue"] = "first-line" }));

        var field = Entities.Models.ModelSchemas.SchemaOf(typeof(Ticket)).Find("summary")!;
        Assert.Equal("Umb.PropertyEditorUi.TextArea", field.Editor!.Alias);
        Assert.Equal("first-line", field.Meta!["acme.support"]!["queue"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("color", """{ "a": 1 }""")]
    [InlineData("Acme.Color", """{ "a": 1 }""")]
    [InlineData("acme.", """{ "a": 1 }""")]
    [InlineData("acme.color", "\"red\"")]
    [InlineData("acme.color", "[1, 2]")]
    public void A_metadata_key_is_owner_dot_name_in_lower_case_and_its_value_is_an_object(string key, string json)
        => Assert.Throws<ArgumentException>(() => UiHints.WithMeta(null, key, JsonNode.Parse(json)));

    [Fact]
    public void A_metadata_value_over_4_KB_is_refused_because_the_schema_is_read_on_every_call()
    {
        var big = new JsonObject { ["text"] = new string('x', UiHints.MaxMetaBytes) };
        Assert.Throws<ArgumentException>(() => UiHints.WithMeta(null, "acme.notes", big));

        var bag = UiHints.WithMeta(null, "acme.notes", new JsonObject { ["text"] = "short" });
        Assert.Equal("short", bag["acme.notes"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_form_picks_each_editor_from_the_field_then_the_format_then_the_shape()
    {
        // A UI that has the text area and a date picker for "date", but no Acme theme picker installed.
        var form = new FormEditors(
            registered: ["Umb.PropertyEditorUi.TextArea"],
            formats: new() { ["date"] = "Umb.PropertyEditorUi.DatePicker" });

        // 1. The field names its editor, and it is registered.
        var email = _agent.UiTool("email_send").JsonSchema.GetProperty("properties");
        Assert.Equal("Umb.PropertyEditorUi.TextArea", form.EditorFor(email.GetProperty("body")));

        // 2. The format maps to an editor: every date field, in any ability or entity, gets the date picker.
        var discount = (await _agent.UiOkAsync("entity_getSchema", """{ "entityType": "umbraco-commerce-discount" }"""))
            .GetProperty("fields").GetProperty("properties");
        Assert.Equal("Umb.PropertyEditorUi.DatePicker", form.EditorFor(discount.GetProperty("startDate")));

        // 3. The plain shape picks a default. An editor that isn't installed is ignored, so the theme falls back to a
        // dropdown of its allowed values rather than breaking the form.
        Assert.Equal("Umb.PropertyEditorUi.TextBox", form.EditorFor(email.GetProperty("to")));
        Assert.Equal("Umb.PropertyEditorUi.Dropdown", form.EditorFor(Theme(await _agent.UiOkAsync("entity_getSchema", HeroSchema))));
        var page = (await _agent.UiOkAsync("entity_getSchema", """{ "entityType": "document" }""")).GetProperty("fields").GetProperty("properties");
        Assert.Equal("Umb.PropertyEditorUi.DocumentPicker", form.EditorFor(page.GetProperty("related").GetProperty("items")));
        Assert.Equal("Umb.PropertyEditorUi.Toggle", form.EditorFor(HeroSettings(await _agent.UiOkAsync("entity_getSchema", HeroSchema)).GetProperty("fullWidth")));
    }

    [Fact]
    public async Task Deleting_every_hint_changes_nothing_but_how_a_field_looks()
    {
        // The bag's main rule: if removing it breaks anything, it wasn't metadata. Build the same hero with and without
        // the hints in play: the model never saw them, and validation and saving don't read them.
        var page = Guid.NewGuid();
        await _agent.StartChangeSetAsync("Hero page");
        await _agent.OkAsync("entity_create", $$"""{ "entityType": "document", "key": "{{page}}", "variant": { "culture": "en-US" }, "fields": { "title": "Home" } }""");
        var hero = (await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "document", "key": "{{page}}", "fieldPath": "blocks", "itemType": "hero", "variant": { "culture": "en-US" }, "fields": { "heading": "Hello" } }
            """)).GetProperty("path").GetString();
        // A value Acme's picker might offer is still checked against the allowed values, not the hints.
        var refused = await _agent.FailAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "{{hero}}/settings/theme", "value": "pink" }""");
        Assert.Equal($"{hero}/settings/theme", refused.GetProperty("problems")[0].GetProperty("path").GetString());

        await _agent.OkAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "{{hero}}/settings/theme", "value": "dark" }""");
        var valid = await _agent.OkAsync("changeSet_validate", "{}");
        Assert.True(valid.GetProperty("valid").GetBoolean(), valid.ToString());
    }

    private static JsonElement HeroSettings(JsonElement schema)
        => schema.GetProperty("itemTypes").GetProperty("hero").GetProperty("fields").GetProperty("properties")
            .GetProperty("settings").GetProperty("properties");

    private static JsonElement Theme(JsonElement schema) => HeroSettings(schema).GetProperty("theme");

    public sealed class Ticket
    {
        public string? Summary { get; set; }
    }
}

/// <summary>
/// How a form built from JSON Schema could pick each field's editor, as Automate's loader would: the field's own
/// <c>x-editor</c> if that editor is installed, then the editor registered for the value's <c>format</c>, then a default
/// from the plain shape. It reads no <c>x-meta</c> key it doesn't know, and treats every hint as untrusted.
/// </summary>
public sealed class FormEditors(IReadOnlyCollection<string> registered, Dictionary<string, string> formats)
{
    public string EditorFor(JsonElement field)
    {
        if (field.TryGetProperty("x-editor", out var editor)
            && editor.TryGetProperty("alias", out var alias)
            && registered.Contains(alias.GetString()!))
        {
            return alias.GetString()!;
        }

        if (field.TryGetProperty("format", out var format) && formats.TryGetValue(format.GetString()!, out var byFormat))
        {
            return byFormat;
        }

        if (field.TryGetProperty("enum", out _))
        {
            return "Umb.PropertyEditorUi.Dropdown";
        }

        if (field.TryGetProperty("x-references", out var references))
        {
            var types = references.ValueKind == JsonValueKind.Array ? references.EnumerateArray().Select(t => t.GetString()) : [references.GetString()];
            return types.First() == "media" ? "Umb.PropertyEditorUi.MediaPicker" : "Umb.PropertyEditorUi.DocumentPicker";
        }

        var type = field.TryGetProperty("type", out var t) ? t.ValueKind == JsonValueKind.Array ? t[0].GetString() : t.GetString() : null;
        return type switch
        {
            "boolean" => "Umb.PropertyEditorUi.Toggle",
            "integer" => "Umb.PropertyEditorUi.Integer",
            "number" => "Umb.PropertyEditorUi.Decimal",
            _ => "Umb.PropertyEditorUi.TextBox",
        };
    }
}
