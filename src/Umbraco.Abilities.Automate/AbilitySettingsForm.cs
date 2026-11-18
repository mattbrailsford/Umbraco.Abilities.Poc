using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Automate.Core.Settings;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// Builds an Automate settings form from an ability's input JSON Schema, not from a C# type. One description then serves
/// AI tools, MCP and Automate alike. Each field's editor is picked in this order: the field's own <c>x-editor</c>; the
/// editor mapped to its <c>format</c>; a default from its shape, as Automate infers one from a C# type.
/// </summary>
public static class AbilitySettingsForm
{
    /// <summary>The form for an input schema. Properties are fields in schema order.</summary>
    public static EditableModelSchema Build(JsonElement inputSchema, IReadOnlyDictionary<string, string> formatEditors)
    {
        var required = inputSchema.TryGetProperty("required", out var list)
            ? list.EnumerateArray().Select(r => r.GetString()).ToHashSet()
            : [];
        var fields = new List<EditableModelFieldDescriptor>();
        if (inputSchema.TryGetProperty("properties", out var properties))
        {
            var order = 0;
            foreach (var property in properties.EnumerateObject())
            {
                fields.Add(Field(property.Name, property.Value, required.Contains(property.Name), order++, formatEditors));
            }
        }

        // FUDGE (Type): Automate's form has a CLR type, used to read defaults and validate. A form built from JSON
        // Schema has none: the ability validates its own input when it runs.
        return new EditableModelSchema { Type = typeof(AbilitySettings), Fields = fields };
    }

    private static EditableModelFieldDescriptor Field(string key, JsonElement schema, bool required, int order, IReadOnlyDictionary<string, string> formatEditors)
    {
        var (editor, config) = EditorFor(schema, formatEditors);
        return new EditableModelFieldDescriptor
        {
            Key = key,
            Label = Humanize(key),
            Description = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("description", out var description) ? description.GetString() : null,
            ValueKind = TypeOf(schema) switch
            {
                "string" => EditableModelValueKind.String,
                "array" => EditableModelValueKind.Collection,
                _ => EditableModelValueKind.Scalar,
            },
            EditorUiAlias = editor,
            EditorConfig = config,
            DefaultValue = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("default", out var preset) ? preset.Clone() : null,
            IsRequired = required,
            SortOrder = order,

            // Every value can be a binding to the trigger's or an earlier step's output. The action resolves them
            // itself (see AbilityAction), so typed values keep their type.
            SupportsBindings = true,

            // FUDGE (PropertyType): Automate's field describes a C# property. There is none; the value is JSON.
            PropertyType = typeof(JsonNode),
        };
    }

    /// <summary>The editor for a field, and its configuration in Automate's form (a list of alias and value).</summary>
    public static (string Editor, string? Config) EditorFor(JsonElement schema, IReadOnlyDictionary<string, string> formatEditors)
    {
        // A schema of true is "any value": nothing to read, so it's edited as JSON.
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return ("Umb.PropertyEditorUi.CodeEditor", Config([("language", "json"), ("height", 160), ("wordWrap", true)]));
        }

        // 1. The field names its editor. Whether that editor is installed is the browser's to know; Automate's form
        // falls back as it does for any editor alias.
        if (schema.TryGetProperty("x-editor", out var named) && named.TryGetProperty("alias", out var alias))
        {
            var config = named.TryGetProperty("config", out var settings) && settings.ValueKind == JsonValueKind.Object
                ? Config(settings.EnumerateObject().Select(p => (p.Name, (JsonNode?)JsonNode.Parse(p.Value.GetRawText()))))
                : null;
            return (alias.GetString()!, config);
        }

        // 2. The value's kind maps to an editor, for every field of that kind.
        if (schema.TryGetProperty("format", out var format) && formatEditors.TryGetValue(format.GetString()!, out var byFormat))
        {
            return (byFormat, null);
        }

        // 3. The plain shape, with Automate's own editors for each.
        if (schema.TryGetProperty("enum", out var values))
        {
            var items = new JsonArray(values.EnumerateArray()
                .Where(v => v.ValueKind != JsonValueKind.Null)
                .Select(v => (JsonNode?)new JsonObject { ["name"] = v.ToString(), ["value"] = v.ToString() })
                .ToArray());
            return ("Umb.PropertyEditorUi.Dropdown", Config([("items", items)]));
        }

        if (schema.TryGetProperty("x-references", out var references))
        {
            var first = references.ValueKind == JsonValueKind.Array ? references[0].GetString() : references.GetString();
            return (first == "media" ? "Umb.PropertyEditorUi.MediaEntityPicker" : "Umb.PropertyEditorUi.DocumentPicker", null);
        }

        return TypeOf(schema) switch
        {
            "boolean" => ("Umb.PropertyEditorUi.Toggle", null),
            "integer" => ("Umb.PropertyEditorUi.Integer", null),
            "number" => ("Umb.PropertyEditorUi.Decimal", null),
            "string" => ("Umb.PropertyEditorUi.TextBox", null),

            // An object or a list whose shape the form can't draw: edited as JSON, as Automate's own JSON settings are.
            _ => ("Umb.PropertyEditorUi.CodeEditor", Config([("language", "json"), ("height", 160), ("wordWrap", true)])),
        };
    }

    private static string Config(IEnumerable<(string Alias, JsonNode? Value)> settings)
        => new JsonArray(settings.Select(s => (JsonNode?)new JsonObject { ["alias"] = s.Alias, ["value"] = s.Value }).ToArray()).ToJsonString();

    // The schema's type, the first that isn't null: "string", ["string", "null"] and ["null", "string"] are all strings.
    private static string? TypeOf(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!schema.TryGetProperty("type", out var type))
        {
            return schema.TryGetProperty("properties", out _) ? "object" : null;
        }

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t != "null")
            : type.GetString();
    }

    // "entityType" → "Entity type".
    private static string Humanize(string key)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(key, "(?<=[a-z0-9])([A-Z])", " $1").ToLowerInvariant();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }
}

/// <summary>
/// A step's settings for an ability: the ability's input, as JSON. Automate only hands an action its settings when the
/// action has a settings type, so this is that type.
/// </summary>
public sealed class AbilitySettings
{
    /// <summary>The input values, by the input schema's property names. Values may hold <c>${ … }</c> bindings.</summary>
    public JsonObject Values { get; set; } = [];
}
