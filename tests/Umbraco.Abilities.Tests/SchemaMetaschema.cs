using System.Text.Json;

using Corvus.Json;

using MetaschemaValidator = Corvus.Json.Validator.JsonSchema;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// Checks schemas against <c>entity-schema.metaschema.json</c>: JSON Schema 2020-12's own meta-schema (a schema for
/// schemas) plus the framework's x- keywords. Shared by the in-memory and the real Umbraco tests.
/// </summary>
public static class SchemaMetaschema
{
    private static readonly MetaschemaValidator Validator = MetaschemaValidator.FromFile(
        Path.Combine(AppContext.BaseDirectory, "entity-schema.metaschema.json"));

    /// <summary>True when the schema follows the meta-schema.</summary>
    public static bool IsValid(JsonElement schema) => Validator.Validate(schema).IsValid;

    /// <summary>
    /// What is wrong with a schema, one line each: where in the schema, and which meta-schema rule it breaks. Empty when
    /// it is valid. An "allOf failed" line only repeats a failure below it, so it is left out.
    /// </summary>
    public static IEnumerable<string> Problems(string name, JsonElement schema)
    {
        var result = Validator.Validate(schema, ValidationLevel.Detailed);
        if (result.IsValid)
        {
            return [];
        }

        var problems = result.Results
            .Where(r => !r.Valid && r.Message?.Contains("allOf failed") != true)
            .Select(r => $"{name}: {r.Location?.DocumentLocation} breaks {r.Location?.ValidationLocation} {r.Message}".TrimEnd())
            .Distinct()
            .ToList();
        return problems.Count > 0 ? problems : [$"{name}: is not valid JSON Schema."];
    }

    /// <summary>
    /// Checks every schema <c>entity.getSchema</c> gives for some entity types: each subtype's fields, and the fields of
    /// every item type they can hold, at any depth. Returns how many schemas it checked, and the problems.
    /// </summary>
    public static async Task<(int Checked, IReadOnlyList<string> Problems)> CheckEntitySchemasAsync(
        IEnumerable<string> entityTypes, Func<string, Task<JsonElement>> getSchema)
    {
        var problems = new List<string>();
        var checkedSchemas = 0;
        foreach (var entityType in entityTypes)
        {
            var top = await getSchema(Arguments(entityType));
            var subtypes = top.TryGetProperty("subtypes", out var list)
                ? list.EnumerateArray().Select(s => s.GetProperty("alias").GetString()).ToList()
                : [null];

            foreach (var subtype in subtypes)
            {
                var name = subtype is null ? entityType : $"{entityType}/{subtype}";
                var output = await getSchema(Arguments(entityType, subtype));
                problems.AddRange(Problems(name, output.GetProperty("fields")));
                checkedSchemas++;

                // An item type's fields can hold items too: ask again until no new item type turns up.
                var seen = new HashSet<string>();
                while (ItemTypesListedIn(output).Where(seen.Add).ToArray() is { Length: > 0 } found)
                {
                    output = await getSchema(Arguments(entityType, subtype, found));
                    foreach (var detail in output.GetProperty("itemTypes").EnumerateObject())
                    {
                        problems.AddRange(Problems($"{name} item type {detail.Name}", detail.Value.GetProperty("fields")));
                        checkedSchemas++;
                    }
                }
            }
        }

        return (checkedSchemas, problems.Distinct().ToList());
    }

    private static IEnumerable<string> ItemTypesListedIn(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().SelectMany(p => p.Name == "x-item-types"
            ? p.Value.EnumerateArray().Select(t => $"{t.GetProperty("group").GetString()}:{t.GetProperty("alias").GetString()}")
            : ItemTypesListedIn(p.Value)),
        JsonValueKind.Array => node.EnumerateArray().SelectMany(ItemTypesListedIn),
        _ => [],
    };

    private static string Arguments(string entityType, string? subtype = null, string[]? itemTypes = null)
        => JsonSerializer.Serialize(new { entityType, subtype, itemTypes });
}
