using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Entities;
using Umbraco.Automate.Core.Settings;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// Builds an Automate settings form from an ability's input JSON Schema, not from a C# type. One description then serves
/// AI tools, MCP and Automate alike. Each field's editor is picked in this order: the field's own <c>x-editor</c>; the
/// entity picker for a key to an entity (<c>x-references</c>); the editor mapped to its <c>format</c>; a default from its
/// shape, as Automate infers one from a C# type.
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
            // A field another field's picker is within, such as the store a discount must be in, shapes the form: its
            // value has to be known while the step is built, so it is chosen, never bound.
            var shaping = properties.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty(SchemaKeywords.ReferencesWithin, out _))
                .Select(p => p.Value.GetProperty(SchemaKeywords.ReferencesWithin).GetString())
                .ToHashSet();

            var order = 0;
            foreach (var property in properties.EnumerateObject())
            {
                fields.Add(Field(property.Name, property.Value, required.Contains(property.Name), order++, formatEditors, bindable: !shaping.Contains(property.Name)));
            }
        }

        // FUDGE (Type): Automate's form has a CLR type, used to read defaults and validate. A form built from JSON
        // Schema has none: the ability validates its own input when it runs.
        return new EditableModelSchema { Type = typeof(AbilitySettings), Fields = fields };
    }

    private static EditableModelFieldDescriptor Field(string key, JsonElement schema, bool required, int order, IReadOnlyDictionary<string, string> formatEditors, bool bindable)
    {
        var (editor, settings) = EditorFor(schema, formatEditors);
        var config = settings is null ? null : Config(settings.Select(p => (p.Key, p.Value?.DeepClone())));
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

            // A value can be a binding to the trigger's or an earlier step's output, unless it shapes the form. The
            // action resolves them itself (see AbilityAction), so typed values keep their type.
            SupportsBindings = bindable,

            // FUDGE (PropertyType): Automate's field describes a C# property. There is none; the value is JSON.
            PropertyType = typeof(JsonNode),
        };
    }

    /// <summary>
    /// The editor for a key to an entity: a picker of the entity type's entities, found through <c>entity.find</c> as
    /// the person running the form, so it works for any entity type with an adapter.
    /// </summary>
    public const string EntityPickerEditor = "Umb.Abilities.PropertyEditorUi.EntityPicker";

    // The entity picker's settings. A key within another field's entity names that field: FUDGE (dependent config)
    // the picker reads it from the form itself, as Automate can't give an editor settings that follow another field.
    private static JsonObject EntityPickerConfig(JsonElement schema, string entityType)
    {
        var config = new JsonObject { ["entityType"] = entityType, ["consumer"] = AutomateConsumer.ConsumerAlias };
        if (schema.TryGetProperty(SchemaKeywords.ReferencesWithin, out var within))
        {
            config["withinField"] = within.GetString();
        }

        return config;
    }

    /// <summary>The editor for a field, and its configuration by alias, as <c>x-editor</c> holds them.</summary>
    public static (string Editor, JsonObject? Config) EditorFor(JsonElement schema, IReadOnlyDictionary<string, string> formatEditors)
    {
        // A schema of true is "any value": nothing to read, so it's edited as JSON.
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return ("Umb.PropertyEditorUi.CodeEditor", JsonEditorConfig());
        }

        // 1. The field names its editor. Whether that editor is installed is the browser's to know; Automate's form
        // falls back as it does for any editor alias.
        if (schema.TryGetProperty(UiHints.EditorKeyword, out var named) && named.TryGetProperty("alias", out var alias))
        {
            var config = named.TryGetProperty("config", out var settings) && settings.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(settings.GetRawText())!.AsObject()
                : null;
            return (alias.GetString()!, config);
        }

        // 2. A key to an entity gets a picker: Umbraco's own for documents and media, and for any other entity type the
        // entity picker, which lists that type's entities through entity.find.
        if (schema.TryGetProperty(SchemaKeywords.References, out var references))
        {
            var first = references.ValueKind == JsonValueKind.Array ? references[0].GetString() : references.GetString();
            return first switch
            {
                Umbraco.Cms.Core.Constants.UdiEntityType.Media => ("Umb.PropertyEditorUi.MediaEntityPicker", null),
                Umbraco.Cms.Core.Constants.UdiEntityType.Document => ("Umb.PropertyEditorUi.DocumentPicker", null),
                _ => (EntityPickerEditor, EntityPickerConfig(schema, first!)),
            };
        }

        // 3. The value's kind maps to an editor, for every field of that kind.
        if (schema.TryGetProperty("format", out var format) && formatEditors.TryGetValue(format.GetString()!, out var byFormat))
        {
            return (byFormat, null);
        }

        // 4. The plain shape, with Automate's own editors for each. A labelled list (oneOf of const and title) shows
        // each value's name, such as a page type's.
        if (schema.TryGetProperty("oneOf", out var labelled) && labelled.EnumerateArray().All(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("const", out _)))
        {
            var items = new JsonArray(labelled.EnumerateArray()
                .Select(o => (JsonNode?)new JsonObject
                {
                    ["name"] = o.TryGetProperty("title", out var title) ? title.GetString() : o.GetProperty("const").ToString(),
                    ["value"] = o.GetProperty("const").ToString(),
                })
                .ToArray());
            return ("Umb.PropertyEditorUi.Dropdown", new JsonObject { ["items"] = items });
        }

        if (schema.TryGetProperty("enum", out var values))
        {
            var items = new JsonArray(values.EnumerateArray()
                .Where(v => v.ValueKind != JsonValueKind.Null)
                .Select(v => (JsonNode?)new JsonObject { ["name"] = v.ToString(), ["value"] = v.ToString() })
                .ToArray());
            return ("Umb.PropertyEditorUi.Dropdown", new JsonObject { ["items"] = items });
        }

        return TypeOf(schema) switch
        {
            "boolean" => ("Umb.PropertyEditorUi.Toggle", null),
            "integer" => ("Umb.PropertyEditorUi.Integer", null),
            "number" => ("Umb.PropertyEditorUi.Decimal", null),
            "string" => ("Umb.PropertyEditorUi.TextBox", null),

            // An object or a list whose shape the form can't draw: edited as JSON, as Automate's own JSON settings are.
            _ => ("Umb.PropertyEditorUi.CodeEditor", JsonEditorConfig()),
        };
    }

    private static JsonObject JsonEditorConfig() => new() { ["language"] = "json", ["height"] = 160, ["wordWrap"] = true };

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
