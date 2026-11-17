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
        var problems = _agent.Tools.Values
            .SelectMany(tool => SchemaMetaschema.Problems(tool.Name, tool.JsonSchema))
            .ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Every_entity_schema_and_item_type_schema_is_valid_JSON_Schema()
    {
        var (checkedSchemas, problems) = await SchemaMetaschema.CheckEntitySchemasAsync(
            _site.Adapters.All.Select(a => a.EntityType),
            arguments => _agent.OkAsync("entity_getSchema", arguments));

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
    [InlineData("""{ "type": "boolean", "x-role": true }""")]
    public void The_meta_schema_catches_a_mistake_in_a_field_deep_inside_a_schema(string field)
    {
        // A type JSON Schema doesn't have, a null description, an x- keyword the framework doesn't define, an item type
        // without its group and name, and variations that aren't a list. Each sits two levels down, as a real field would.
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
                "blocks": { "type": "array", "x-edit": "Items.", "x-item-types": [{ "alias": "hero", "group": "blocks", "name": "Hero" }] }
              }
            }
            """);

        Assert.True(SchemaMetaschema.IsValid(schema.RootElement));
    }
}
