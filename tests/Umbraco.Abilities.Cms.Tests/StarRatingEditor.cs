using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.PropertyEditors;

namespace Umbraco.Abilities.Cms.Tests;

/// <summary>
/// A third-party property editor, as a package would ship it: a star rating, stored as JSON,
/// <c>{ "stars": 4, "of": 5 }</c>. Umbraco knows nothing about its value beyond that it is JSON.
/// </summary>
[DataEditor(StarRatingPropertyEditor.Alias, ValueType = ValueTypes.Json)]
public sealed class StarRatingPropertyEditor(IDataValueEditorFactory dataValueEditorFactory) : DataEditor(dataValueEditorFactory)
{
    public const string Alias = "Acme.StarRating";
}

/// <summary>
/// The package's handler for its editor: the only code it writes so the editor works with the entity abilities. Without
/// it, a rating is a whole JSON value a model must get right. With it, a rating is a whole number from 1 to 5, checked
/// before Umbraco sees it, and the stored shape is the handler's business.
/// </summary>
public sealed class StarRatingHandler : IPropertyEditorEntityHandler
{
    private const int Stars = 5;

    public bool Handles(string editorAlias) => editorAlias == StarRatingPropertyEditor.Alias;

    public Task<FieldDefinition> DescribeAsync(PropertyEditorContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(context.Field(FieldType.Integer) with
        {
            Description = $"{context.Description} A rating in stars, 1 to {Stars}.",
            Rules = [new RangeAttribute(1, Stars)],
        });

    public Task<JsonNode?> ReadAsync(JsonNode stored, PropertyValueReader reader, CancellationToken cancellationToken = default)
    {
        // Stored JSON can come back as text or as an object.
        var rating = stored is JsonValue text && text.GetValueKind() == JsonValueKind.String ? JsonNode.Parse(text.GetValue<string>()) : stored;
        return Task.FromResult(rating?["stars"]?.DeepClone());
    }

    public Task<JsonNode?> WriteAsync(JsonNode value, PropertyValueWriter writer, CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode?>(new JsonObject { ["stars"] = value.DeepClone(), ["of"] = Stars });
}
