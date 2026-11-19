using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.ContentEditing.Validation;
using Umbraco.Cms.Infrastructure.Serialization;
using Umbraco.Extensions;

namespace Umbraco.Abilities.Cms;

/// <summary>
/// An entity as the CMS's editing services take it: names as variants, property values in editor format, and how to
/// turn the CMS's validation errors back into entity paths.
/// </summary>
public sealed class ContentValues
{
    internal ContentValues(Guid key, Guid typeKey, Guid? parentKey, List<VariantModel> variants, List<StoredValue> values, PropertyErrorLocators errors)
    {
        Key = key;
        TypeKey = typeKey;
        ParentKey = parentKey;
        Variants = variants;
        Values = values;
        Errors = errors;
    }

    private static readonly JsonSerializerOptions ModelValueOptions = new() { Converters = { new JsonObjectConverter() } };

    public Guid Key { get; }

    public Guid TypeKey { get; }

    public Guid? ParentKey { get; }

    /// <summary>The names, one variant per culture and segment that has values.</summary>
    public List<VariantModel> Variants { get; }

    /// <summary>The property values in editor format.</summary>
    public List<StoredValue> Values { get; }

    /// <summary>The property values as the editing services take them.</summary>
    public List<PropertyValueModel> Properties
        => Values.Select(v => new PropertyValueModel { Alias = v.Alias, Value = ToModelValue(v.Value), Culture = v.Culture, Segment = v.Segment }).ToList();

    /// <summary>The cultures the item has, to validate only those: validating others fails values that don't exist there.</summary>
    public ISet<string>? Cultures => Variants.Any(v => v.Culture is not null) ? Variants.Select(v => v.Culture).OfType<string>().ToHashSet() : null;

    internal PropertyErrorLocators Errors { get; }

    /// <summary>A CMS validation error as a problem on the entity path the value came from.</summary>
    public Problem ToProblem(PropertyValidationError error)
    {
        // Same fields as Umbraco's error; only the alias and JSON path become one entity path.
        var located = new Problem(error.Alias, error.ErrorMessages.ToList()).In(CmsVariants.Of(error.Culture, error.Segment));
        if (string.IsNullOrEmpty(error.JsonPath) || error.JsonPath == "$")
        {
            return located;
        }

        return Errors.Locate(error.Alias, error.Culture, error.Segment, error.JsonPath) is { } at
            ? located with { Path = at.Path, Variant = CmsVariants.Of(at.Culture, at.Segment).ToJson() }
            : located with { Path = $"{error.Alias} {error.JsonPath}" };
    }

    internal static object? ToModelValue(JsonNode? node)
        => node is null ? null : JsonSerializer.Deserialize<object?>(node.ToJsonString(), ModelValueOptions);
}
