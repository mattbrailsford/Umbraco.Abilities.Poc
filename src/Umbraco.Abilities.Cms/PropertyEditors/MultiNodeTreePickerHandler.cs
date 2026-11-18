using System.Text.Json.Nodes;

using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DynamicRoot;
using Umbraco.Cms.Core.DynamicRoot.QuerySteps;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Abilities.Cms.PropertyEditors;

/// <summary>
/// The multi-node tree picker as a list of references: the field's value is the picked keys, marked with
/// <c>x-references</c>, so a caller knows to find them and validation checks they exist. The description says what the
/// data type allows: which types, from under which node, and how many.
/// <para>
/// Umbraco stores it as <c>[{ "type": "document", "unique": "…" }]</c>. A picker of members has no entity adapter here,
/// so it stays a whole JSON value, through the fallback.
/// </para>
/// </summary>
public sealed class MultiNodeTreePickerHandler(
    IEntityService? entities,
    IContentTypeService? contentTypes,
    IMediaTypeService? mediaTypes,
    IDynamicRootService? dynamicRoots,
    IPropertyEditorEntityHandler fallback) : IPropertyEditorEntityHandler
{
    public bool Handles(string editorAlias) => editorAlias == Constants.PropertyEditors.Aliases.MultiNodeTreePicker;

    public async Task<FieldDefinition> DescribeAsync(PropertyEditorContext context, CancellationToken cancellationToken = default)
    {
        var startNode = context.Setting("startNode") as JsonObject;
        if (EntityTypeOf(startNode?["type"]?.ToString()) is not { } entityType)
        {
            return await fallback.DescribeAsync(context, cancellationToken);
        }

        var limits = new List<string>();
        if (AllowedTypes(context.Setting("filter")?.ToString(), entityType) is { Count: > 0 } types)
        {
            limits.Add($"Only of these types: {string.Join(", ", types)}.");
        }

        if (await StartNodeNameAsync(startNode!) is { } under)
        {
            limits.Add($"Only from under '{under}'.");
        }

        var (min, max) = (Number(context.Setting("minNumber")), Number(context.Setting("maxNumber")));
        if (max > 0)
        {
            limits.Add(max == 1 ? "At most one." : $"At most {max}.");
        }

        if (min > 0)
        {
            limits.Add($"At least {min}.");
        }

        var field = context.Field(FieldType.EntityReference) with { ReferencesEntityType = entityType, Many = true };
        var description = field.Description.TrimEnd();
        if (description.Length > 0 && !".!?".Contains(description[^1]))
        {
            description += ".";
        }

        return limits.Count == 0 ? field : field with { Description = $"{description} {string.Join(' ', limits)}" };
    }

    public async Task<JsonNode?> ReadAsync(JsonNode stored, PropertyValueReader reader, CancellationToken cancellationToken = default)
    {
        if (reader.EntityField.Type != FieldType.EntityReference)
        {
            return await fallback.ReadAsync(stored, reader, cancellationToken);
        }

        var keys = (stored as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(picked => picked["unique"]?.ToString())
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => (JsonNode?)JsonValue.Create(key));
        return new JsonArray(keys.ToArray());
    }

    public async Task<JsonNode?> WriteAsync(JsonNode value, PropertyValueWriter writer, CancellationToken cancellationToken = default)
    {
        if (writer.EntityField.Type != FieldType.EntityReference)
        {
            return await fallback.WriteAsync(value, writer, cancellationToken);
        }

        var type = writer.EntityField.ReferencesEntityType;
        var picked = (value as JsonArray ?? [value])
            .Where(key => key is not null)
            .Select(key => (JsonNode?)new JsonObject { ["type"] = type, ["unique"] = key!.ToString() });
        return new JsonArray(picked.ToArray());
    }

    // The tree the picker shows, as the entity type of what it picks. Members have no adapter here.
    private static string? EntityTypeOf(string? treeType) => treeType switch
    {
        null or "" or "content" => CmsDocumentAdapter.Type,
        "media" => CmsMediaAdapter.Type,
        _ => null,
    };

    // The data type's filter: content or media type keys (or aliases, in older data), as aliases.
    private List<string> AllowedTypes(string? filter, string entityType)
        => (filter ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => Guid.TryParse(item, out var key)
                ? entityType == CmsMediaAdapter.Type ? mediaTypes?.Get(key)?.Alias : contentTypes?.Get(key)?.Alias
                : item)
            .OfType<string>()
            .ToList();

    // The node the picker starts from, by name: a fixed node, or a dynamic root that resolves to one node. A dynamic root
    // relative to the page being edited depends on that page, so only one with a fixed origin is named.
    private async Task<string?> StartNodeNameAsync(JsonObject startNode)
    {
        if (entities is null)
        {
            return null;
        }

        try
        {
            if (Guid.TryParse(startNode["id"]?.ToString(), out var id))
            {
                return entities.Get(id)?.Name;
            }

            if (startNode["dynamicRoot"] is JsonObject dynamicRoot
                && dynamicRoots is not null
                && dynamicRoot["originAlias"]?.ToString() is "ByKey" or "Root"
                && Guid.TryParse(dynamicRoot["originKey"]?.ToString(), out var origin))
            {
                var roots = (await dynamicRoots.GetDynamicRootsAsync(new DynamicRootNodeQuery
                {
                    OriginAlias = dynamicRoot["originAlias"]!.ToString(),
                    OriginKey = origin,
                    Context = new DynamicRootContext { CurrentKey = null, ParentKey = origin },
                    QuerySteps = (dynamicRoot["querySteps"] as JsonArray ?? [])
                        .OfType<JsonObject>()
                        .Select(step => new DynamicRootQueryStep
                        {
                            Alias = step["alias"]?.ToString() ?? string.Empty,
                            AnyOfDocTypeKeys = (step["anyOfDocTypeKeys"] as JsonArray ?? [])
                                .Select(k => Guid.TryParse(k?.ToString(), out var key) ? key : Guid.Empty)
                                .Where(k => k != Guid.Empty)
                                .ToList(),
                        })
                        .ToList(),
                })).ToList();
                return roots.Count == 1 ? entities.Get(roots[0])?.Name : null;
            }
        }
        catch (Exception)
        {
            // A start node that can't be resolved just isn't named.
        }

        return null;
    }

    private static int Number(JsonNode? node) => int.TryParse(node?.ToString(), out var number) ? number : 0;
}
