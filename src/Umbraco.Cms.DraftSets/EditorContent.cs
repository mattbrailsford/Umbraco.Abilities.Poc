using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.ContentEditing.Validation;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Infrastructure.Serialization;
using Umbraco.Extensions;

namespace Umbraco.Cms.DraftSets;

/// <summary>
/// Documents in editor format (what the Management API sends): read live, compared value by value, merged, validated
/// and saved through <see cref="IContentEditingService"/>. Shared by both draft set stand-ins and the branch stand-in.
/// </summary>
internal sealed class EditorContent(
    IContentEditingService editing,
    PropertyEditorCollection editors,
    IJsonSerializer json,
    IIdKeyMap idKeys)
{
    private static readonly JsonSerializerOptions ModelValueOptions = new() { Converters = { new JsonObjectConverter() } };

    /// <summary>The live content in editor format, as the backoffice loads it.</summary>
    public async Task<DraftContent?> GetLiveAsync(GuidUdi entity)
    {
        var key = DocumentKey(entity);
        var content = await editing.GetAsync(key);
        if (content is null || content.Trashed)
        {
            return null; // Content in the recycle bin is deleted, as far as editing it goes.
        }

        var names = content.ContentType.VariesByCulture()
            ? content.AvailableCultures.ToDictionary(c => c, c => content.GetCultureName(c) ?? string.Empty)
            : new Dictionary<string, string> { [string.Empty] = content.Name ?? string.Empty };

        var values = new List<DraftValue>();
        foreach (var property in content.Properties)
        {
            foreach (var value in property.Values)
            {
                if (ReadEditorValue(property, value.Culture, value.Segment) is { } node)
                {
                    values.Add(new DraftValue(property.Alias, value.Culture, value.Segment, node));
                }
            }
        }

        var parent = content.ParentId == Constants.System.Root ? (Guid?)null : idKeys.GetKeyForId(content.ParentId, UmbracoObjectTypes.Document).Result;
        return new DraftContent { Key = key, ContentTypeKey = content.ContentType.Key, ParentKey = parent, Names = names, Values = values, Version = content.UpdateDate.Ticks };
    }

    /// <summary>Umbraco's own validation of the content, as it would be saved.</summary>
    public async Task<IReadOnlyList<PropertyValidationError>> ValidateAsync(DraftContent content, Guid userKey)
    {
        var attempt = content.IsNew
            ? await editing.ValidateCreateAsync(ToCreateModel(content), userKey)
            : await editing.ValidateUpdateAsync(content.Key, new ValidateContentUpdateModel { Variants = Variants(content), Properties = Properties(content), Cultures = Cultures(content) }, userKey);
        return attempt.Result.ValidationErrors.ToList();
    }

    /// <summary>
    /// Creates or updates the content. Umbraco saves invalid values and reports them afterwards, so any reported problem
    /// throws, for the caller's scope to roll back.
    /// </summary>
    public async Task<IContent?> SaveAsync(DraftContent content, Guid userKey)
    {
        ContentEditingOperationStatus status;
        ContentValidationResult validation;
        IContent? saved;
        if (content.IsNew)
        {
            var created = await editing.CreateAsync(ToCreateModel(content), userKey);
            (status, validation, saved) = (created.Status, created.Result.ValidationResult, created.Result.Content);
        }
        else
        {
            var updated = await editing.UpdateAsync(content.Key, new ContentUpdateModel { Variants = Variants(content), Properties = Properties(content) }, userKey);
            (status, validation, saved) = (updated.Status, updated.Result.ValidationResult, updated.Result.Content);
        }

        // Umbraco reports problems in every culture; only those in cultures the document has (or invariant) fail it.
        var cultures = Cultures(content);
        var errors = validation.ValidationErrors.Where(e => e.Culture is null || cultures is null || cultures.Contains(e.Culture)).ToList();
        if (status is not (ContentEditingOperationStatus.Success or ContentEditingOperationStatus.PropertyValidationError) || errors.Count > 0)
        {
            var details = string.Join("; ", errors.Select(e => $"{e.Alias}{(e.Culture is null ? string.Empty : $" ({e.Culture})")}: {string.Join(" ", e.ErrorMessages)}"));
            throw new InvalidOperationException($"Umbraco did not save {content.Names.Values.FirstOrDefault() ?? content.Key.ToString()}: {status}. {details}".Trim());
        }

        return saved;
    }

    /// <summary>This stand-in handles documents; the UDI address leaves room for media and other types.</summary>
    public static Guid DocumentKey(GuidUdi entity)
        => entity.EntityType == Constants.UdiEntityType.Document
            ? entity.Guid
            : throw new NotSupportedException($"This draft set handles documents, not {entity.EntityType}.");

    /// <summary>Every name, the parent and every property value, as <see cref="DraftValue"/>s.</summary>
    public static IEnumerable<DraftValue> AllValues(DraftContent content)
        => content.Names.Select(n => new DraftValue(DraftSetService.NameAlias, n.Key.Length == 0 ? null : n.Key, null, n.Value))
            .Append(new DraftValue(DraftSetService.ParentAlias, null, null, ParentValue(content)))
            .Concat(content.Values);

    /// <summary>The values that differ between two versions of the same content, as they are in <paramref name="to"/>.</summary>
    public static IEnumerable<DraftValue> Diff(DraftContent from, DraftContent to)
        => AllValues(from).Concat(AllValues(to))
            .DistinctBy(v => v.Id)
            .Where(v => !JsonNode.DeepEquals(ValueIn(from, v), ValueIn(to, v)))
            .Select(v => v with { Value = ValueIn(to, v)?.DeepClone() });

    public static JsonNode? ValueIn(DraftContent content, DraftValue value)
        => value.Alias switch
        {
            DraftSetService.NameAlias => content.Names.TryGetValue(value.Culture ?? string.Empty, out var name) ? JsonValue.Create(name) : null,
            DraftSetService.ParentAlias => ParentValue(content),
            _ => content.Values.FirstOrDefault(v => v.Id == value.Id)?.Value,
        };

    public static void Overlay(DraftContent content, DraftValue change)
    {
        if (change.Alias == DraftSetService.ParentAlias)
        {
            content.ParentKey = change.Value is null ? null : Guid.Parse(change.Value.GetValue<string>());
            return;
        }

        if (change.Alias == DraftSetService.NameAlias)
        {
            if (change.Value is null)
            {
                content.Names.Remove(change.Culture ?? string.Empty);
            }
            else
            {
                content.Names[change.Culture ?? string.Empty] = change.Value.GetValue<string>();
            }

            return;
        }

        content.Values.RemoveAll(v => v.Id == change.Id);
        if (change.Value is not null)
        {
            content.Values.Add(change with { Value = change.Value.DeepClone() });
        }
    }

    private static JsonNode? ParentValue(DraftContent content) => content.ParentKey is { } parent ? JsonValue.Create(parent.ToString()) : null;

    /// <summary>The value in editor format, as the backoffice loads it: through the property editor's ToEditor.</summary>
    private JsonNode? ReadEditorValue(IProperty property, string? culture, string? segment)
    {
        var value = editors.TryGet(property.PropertyType.PropertyEditorAlias, out var editor)
            ? editor.GetValueEditor().ToEditor(property, culture, segment)
            : property.GetValue(culture, segment);
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            _ => JsonNode.Parse(json.Serialize(value)),
        };
    }

    private static ContentCreateModel ToCreateModel(DraftContent content) => new()
    {
        Key = content.Key,
        ContentTypeKey = content.ContentTypeKey,
        ParentKey = content.ParentKey,
        Variants = Variants(content),
        Properties = Properties(content),
    };

    /// <summary>The cultures the document has, so validation doesn't fail cultures it doesn't exist in; null when invariant.</summary>
    private static HashSet<string>? Cultures(DraftContent content)
        => content.Names.Keys.Any(c => c.Length > 0) ? content.Names.Keys.Where(c => c.Length > 0).ToHashSet() : null;

    private static List<VariantModel> Variants(DraftContent content)
        => content.Names.Select(n => new VariantModel { Culture = n.Key.Length == 0 ? null : n.Key, Name = n.Value }).ToList();

    // Templates aren't carried yet: a real implementation re-sends the current template key, because a null TemplateKey clears it.
    private static List<PropertyValueModel> Properties(DraftContent content)
        => content.Values.Select(v => new PropertyValueModel
        {
            Alias = v.Alias,
            Culture = v.Culture,
            Segment = v.Segment,
            Value = v.Value is null ? null : JsonSerializer.Deserialize<object?>(v.Value.ToJsonString(), ModelValueOptions),
        }).ToList();
}
