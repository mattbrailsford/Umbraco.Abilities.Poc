using System.Text.Json.Nodes;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Abilities.Cms.PropertyEditors;

/// <summary>
/// An editor whose stored value is the field's value as it is: a string, a number, a list of tags. Only the field
/// differs per editor, so a handler is one line: <c>new ValueEditorHandler("My.Editor", c => c.Field(FieldType.String))</c>.
/// </summary>
public class ValueEditorHandler(string editorAlias, Func<PropertyEditorContext, FieldDefinition> describe) : IPropertyEditorEntityHandler
{
    public bool Handles(string alias) => alias == editorAlias;

    public Task<FieldDefinition> DescribeAsync(PropertyEditorContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(describe(context));

    public Task<JsonNode?> ReadAsync(JsonNode stored, PropertyValueReader reader, CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode?>(stored.DeepClone());

    public Task<JsonNode?> WriteAsync(JsonNode value, PropertyValueWriter writer, CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode?>(value.DeepClone());

    /// <summary>
    /// The core editors whose values are plain values or references. Defaults are the backoffice's value presets for
    /// each editor: a toggle starts at its "default" setting or off, a number at its "min" setting or 0.
    /// </summary>
    public static IEnumerable<IPropertyEditorEntityHandler> Core =>
    [
        new ValueEditorHandler(Constants.PropertyEditors.Aliases.TextBox, c => c.Field(FieldType.String)),
        new ValueEditorHandler(Constants.PropertyEditors.Aliases.TextArea, c => c.Field(FieldType.String)),
        new ValueEditorHandler(Constants.PropertyEditors.Aliases.Integer, c => c.Field(FieldType.Integer) with { Default = (int)NumberSetting(c, "min") }),
        new ValueEditorHandler(Constants.PropertyEditors.Aliases.Decimal, c => c.Field(FieldType.Number) with { Default = NumberSetting(c, "min") }),
        new ValueEditorHandler(Constants.PropertyEditors.Aliases.Boolean, c => c.Field(FieldType.Boolean) with { Default = BoolSetting(c, "default") }),
        new ValueEditorHandler(Constants.PropertyEditors.Aliases.Tags, c => c.Field(FieldType.String) with { Many = true }),
        new ValueEditorHandler(Constants.PropertyEditors.Aliases.ContentPicker, c => c.Field(FieldType.EntityReference) with { ReferencesEntityType = CmsDocumentAdapter.Type }),
    ];

    private static decimal NumberSetting(PropertyEditorContext context, string alias)
        => context.Setting(alias) is { } node && decimal.TryParse(node.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static bool BoolSetting(PropertyEditorContext context, string alias)
        => context.Setting(alias) is { } node && (node.ToString() is "true" or "True" or "1");
}

/// <summary>
/// The fallback, for an editor no handler maps: the value is set whole, in the editor's own format, described by the
/// editor's value schema when it has one (<see cref="IPropertyEditorSchemaService"/>), and checked by Umbraco when saved.
/// </summary>
public sealed class JsonValueEditorHandler(IPropertyEditorSchemaService? schemas) : IPropertyEditorEntityHandler
{
    public bool Handles(string editorAlias) => true;

    public Task<FieldDefinition> DescribeAsync(PropertyEditorContext context, CancellationToken cancellationToken = default)
    {
        var editor = context.Property.PropertyEditorAlias;
        JsonObject? schema = null;
        try
        {
            schema = schemas?.GetValueSchema(editor, context.DataType?.ConfigurationObject);
        }
        catch (Exception)
        {
            // An editor whose schema can't be built is treated like one without a schema.
        }

        var field = context.Field(FieldType.Json) with { JsonSchema = schema };
        return Task.FromResult(schema is null ? field with { Hints = UiHints.WithHint(field.Hints, AbilityAudiences.Model, $"A value in the '{editor}' editor's own format.") } : field);
    }

    public Task<JsonNode?> ReadAsync(JsonNode stored, PropertyValueReader reader, CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode?>(stored.DeepClone());

    public Task<JsonNode?> WriteAsync(JsonNode value, PropertyValueWriter writer, CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode?>(value.DeepClone());
}
