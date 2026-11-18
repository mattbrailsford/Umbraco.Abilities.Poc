using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Consumers;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// Names the editor for every input property the Automate consumer offers (<c>x-editor</c>), from Automate's own rules:
/// the property's own editor, the editor for its format, or one for its shape (<see cref="AbilitySettingsForm.EditorFor"/>).
/// A dependent input, such as the fields of the chosen page type, gets the fields editor, and its resolved fields get
/// their editors too. So anything that draws a step's form, the form itself or the fields editor in the browser, reads
/// one keyword. Automate's own filter, on its own consumer: nothing here is in the framework.
/// </summary>
public sealed class AutomateEditorsFilter(AbilityActionOptions options) : IAbilityFilter, IAbilityResultFilter
{
    /// <summary>The filter's alias, listed by the Automate consumer.</summary>
    public const string FilterAlias = "automate-editors";

    /// <summary>
    /// FUDGE (dependent input): the editor that draws a dependent input, such as a page type's fields, once what it
    /// depends on is chosen. It asks the Management API for the input's schema and draws each field with its editor.
    /// Automate's form can't change with its own values; with that, this editor would go.
    /// </summary>
    public const string DependentInputEditor = "Umb.Abilities.PropertyEditorUi.DependentInput";

    public string Alias => FilterAlias;

    public string Description => "Names Automate's editor for every input property, and the fields editor for a dependent input.";

    public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
    {
        yield return tool with { InputSchema = WithEditors(tool, tool.InputSchema, context) };
    }

    // A resolved input schema comes back through here. A result that isn't a schema has no properties: left as it is.
    public JsonElement ShapeResult(AbilityTool tool, JsonElement result, AbilityFilterContext context)
        => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("properties", out _) ? WithEditors(tool, result, context) : result;

    private JsonElement WithEditors(AbilityTool tool, JsonElement schema, AbilityFilterContext context)
    {
        var node = JsonNode.Parse(schema.GetRawText()) as JsonObject;
        if (node?["properties"] is not JsonObject properties)
        {
            return schema;
        }

        var dependent = tool.Descriptor.Input.DependentInput;
        foreach (var (name, property) in properties)
        {
            if (name != dependent)
            {
                NameEditor(property as JsonObject);
                continue;
            }

            property![UiHints.EditorKeyword] = new FieldEditor(DependentInputEditor, new JsonObject
            {
                ["tool"] = tool.Id,
                ["consumer"] = context.Consumer.Alias,
                ["input"] = name,
                // Only what the form asks for: an argument the tool fixes, such as the entity type, isn't in it.
                ["dependsOn"] = new JsonArray(tool.Descriptor.Input.DependsOn.Where(d => !tool.FixedArguments.ContainsKey(d)).Select(d => (JsonNode?)d).ToArray()),
            }).ToJson();

            // Once resolved, it holds the chosen type's fields: each gets its editor too.
            foreach (var (_, field) in property["properties"] as JsonObject ?? [])
            {
                NameEditor(field as JsonObject);
            }
        }

        return JsonSerializer.SerializeToElement(node);
    }

    // The field's editor, unless it names one.
    private void NameEditor(JsonObject? field)
    {
        if (field is null || field.ContainsKey(UiHints.EditorKeyword))
        {
            return;
        }

        var (editor, config) = AbilitySettingsForm.EditorFor(JsonSerializer.SerializeToElement(field), options.FormatEditors);
        field[UiHints.EditorKeyword] = new FieldEditor(editor, config).ToJson();
    }
}
