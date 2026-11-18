using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Entities;

/// <summary>
/// One setting of a plugin, as the product's registry describes it: Commerce's setting definitions, Automate's
/// <c>[Field]</c> settings. A product maps its own metadata to this; <see cref="ItemTypeFields"/> turns it into a field.
/// </summary>
public sealed record SettingDescriptor(
    string Key,
    string? Label,
    string? Description,
    Type ClrType,
    IReadOnlyList<ValidationAttribute> Rules,
    string? EditorUiAlias = null,
    int SortOrder = 0,
    bool AcceptsBindings = false)
{
    /// <summary>How to fill the setting, by audience (<c>model</c>, <c>person</c>). See <see cref="FieldDefinition.Hints"/>.</summary>
    public IReadOnlyDictionary<string, string>? Hints { get; init; }
}

/// <summary>Builds item type fields from a product's setting metadata, with the same type mapping as model classes.</summary>
public static class ItemTypeFields
{
    /// <summary>
    /// Text meant for a person, or null when there is none: products often leave a localization key (<c>#uc…Label</c>)
    /// that only the backoffice resolves, which tells a model nothing.
    /// </summary>
    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) || value.StartsWith('#') ? null : value;

    /// <summary>
    /// A field for a setting. <paramref name="editorReferences"/> is the product's table from an editor UI alias to the
    /// entity types it picks (a store picker picks stores). An editor it doesn't know leaves a plain value, and the
    /// description names the editor, so the field still works with less guidance.
    /// </summary>
    public static FieldDefinition For(SettingDescriptor setting, IReadOnlyDictionary<string, string[]> editorReferences)
    {
        var references = setting.EditorUiAlias is { } editor ? editorReferences.GetValueOrDefault(editor) : null;
        // A picker that stores a key as a string is a reference to what it picks.
        var type = references is not null && setting.ClrType == typeof(string) ? typeof(Guid?) : setting.ClrType;
        var allowed = setting.Rules.OfType<AllowedValuesAttribute>().FirstOrDefault()?.Values.Select(v => v?.ToString() ?? string.Empty).ToList();

        var description = string.Join(" ", new[] { Text(setting.Label), Text(setting.Description) }.OfType<string>().Distinct());

        // What a model needs to write the value. A form draws the setting with its own editor, so a person doesn't.
        var hints = setting.Hints;
        if (setting.EditorUiAlias is { } unknown && references is null && !IsPlainEditor(unknown))
        {
            hints = AppendHint(hints, $"Edited with '{unknown}' in the backoffice.");
        }

        if (setting.AcceptsBindings)
        {
            hints = AppendHint(hints, "Can be a binding to an earlier step's output, e.g. ${trigger.orderId}.");
        }

        var field = ModelSchemas.FieldFor(setting.Key, description, type, setting.Rules.OfType<RequiredAttribute>().Any(), references, allowed);
        return field with
        {
            Rules = setting.Rules.Where(r => r is not RequiredAttribute and not AllowedValuesAttribute).ToList(),
            AcceptsBindings = setting.AcceptsBindings,
            Hints = hints,

            // The product's own editor for the setting, so a form shows what its backoffice shows.
            Editor = setting.EditorUiAlias is { } editorUi ? new FieldEditor(editorUi) : null,
        };
    }

    private static IReadOnlyDictionary<string, string> AppendHint(IReadOnlyDictionary<string, string>? hints, string text)
        => UiHints.WithHint(hints, AbilityAudiences.Model, hints?.GetValueOrDefault(AbilityAudiences.Model) is { } earlier ? $"{earlier} {text}" : text);

    private static bool IsPlainEditor(string editorUiAlias)
        => editorUiAlias is "Umb.PropertyEditorUi.TextBox" or "Umb.PropertyEditorUi.Toggle" or "Umb.PropertyEditorUi.Decimal" or "Umb.PropertyEditorUi.Integer";
}
