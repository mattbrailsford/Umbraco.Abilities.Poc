using System.Text.Json;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// A model reads the schemas the framework writes, so they must be real JSON Schema. Each one is checked against a
/// meta-schema (a schema for schemas): JSON Schema 2020-12's own, plus the framework's x- keywords. A typo in a
/// keyword, a null where a string belongs, or an x- keyword nobody defined fails here.
/// </summary>
public class SchemaMetaschemaTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly ScriptedAgent _agent;

    public SchemaMetaschemaTests() => _agent = new ScriptedAgent(_site);

    [Fact]
    public void Every_tools_input_schema_is_valid_JSON_Schema()
    {
        // As a UI reads them, with x-editor and x-meta: a model's copy is the same minus those.
        var problems = _agent.Tools.Keys
            .SelectMany(tool => SchemaMetaschema.Problems(tool, _agent.UiTool(tool).JsonSchema))
            .ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Every_entity_schema_and_item_type_schema_is_valid_JSON_Schema()
    {
        var (checkedSchemas, problems) = await SchemaMetaschema.CheckEntitySchemasAsync(
            _site.Adapters.All.Select(a => a.EntityType),
            arguments => _agent.UiOkAsync("entity_getSchema", arguments));

        Assert.True(checkedSchemas > _site.Adapters.All.Count, $"Only {checkedSchemas} schemas were checked.");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("""{ "type": "text" }""")]
    [InlineData("""{ "type": "string", "description": null }""")]
    [InlineData("""{ "type": "array", "x-item-type": [] }""")]
    [InlineData("""{ "type": "string", "x-oops": 1 }""")]
    [InlineData("""{ "type": "string", "description": "d", "x-edit": "e", "x-oops": 1 }""")]
    [InlineData("""{ "type": "array", "x-item-types": [{ "alias": "hero" }] }""")]
    [InlineData("""{ "type": "string", "x-variations": "culture" }""")]
    [InlineData("""{ "type": "string", "x-editor": "Umb.PropertyEditorUi.TextArea" }""")]
    [InlineData("""{ "type": "string", "x-editor": { "config": {} } }""")]
    [InlineData("""{ "type": "string", "x-meta": { "color": {} } }""")]
    [InlineData("""{ "type": "string", "x-meta": { "Acme.Color": {} } }""")]
    [InlineData("""{ "type": "string", "x-meta": { "acme.color": "red" } }""")]
    [InlineData("""{ "type": "string", "format": "cron" }""")]
    public void The_meta_schema_catches_a_mistake_in_a_field_deep_inside_a_schema(string field)
    {
        // A type JSON Schema doesn't have, a null description, an x- keyword the framework doesn't define, an item type
        // without its group and name, variations that aren't a list, an editor without an alias, and a metadata key
        // without an owner, in capitals, or with a value that isn't an object, and a format that is neither standard nor
        // owned. Each sits two levels down, as a real field would.
        using var schema = JsonDocument.Parse($$"""
            { "type": "object", "properties": { "seo": { "type": "object", "properties": { "field": {{field}} } } } }
            """);

        Assert.False(SchemaMetaschema.IsValid(schema.RootElement));
    }

    [Fact]
    public void The_meta_schema_accepts_a_correct_schema_with_the_frameworks_keywords()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "title": { "type": "string", "description": "Title.", "x-variations": ["culture"], "x-bindings": true },
                "when": { "type": "string", "format": "umbraco.cron" },
                "sent": { "type": "string", "format": "date-time" },
                "theme": { "type": "string", "x-editor": { "alias": "Acme.ThemePicker", "config": { "swatches": true } }, "x-meta": { "acme.theme": { "preview": "hero" } } },
                "blocks": { "type": "array", "x-edit": "Items.", "x-item-types": [{ "alias": "hero", "group": "blocks", "name": "Hero" }] }
              }
            }
            """);

        Assert.True(SchemaMetaschema.IsValid(schema.RootElement));
    }
}
