using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Settings;

namespace Umbraco.Abilities.Automate;

/// <summary>What an entity action does.</summary>
public enum EntityActionKind
{
    /// <summary>Makes a new entity of the subtype, with the values set in the step.</summary>
    Create,

    /// <summary>Changes the values set in the step on an existing entity; values left empty stay as they are.</summary>
    Update,
}

/// <summary>
/// Create or Update one subtype of an entity type, such as "Create Article", as an Automate action. A person builds a
/// step from a form, not from paths: the form is the subtype's own fields, each with the product's editor where the
/// field names one (<c>x-editor</c>). Underneath, the step calls the generic entity abilities (<c>entity.create</c>,
/// <c>entity.setValue</c>) in one change set, so nothing here is per product.
/// <para>
/// Not in the form: fields that hold items, such as blocks or a discount's rules. Building those is many edits, which
/// a form of fixed fields can't describe. The action's description lists them.
/// </para>
/// </summary>
public sealed class EntityAction : IAction
{
    private static readonly JsonSchema Summary = JsonSchema.FromText(AbilitySchemas.OutputSchema(typeof(EntitySummary)).GetRawText());

    private readonly IEntityAdapter _adapter;
    private readonly EntitySubtype? _subtype;
    private readonly EntityActionKind _kind;
    private readonly AbilityStep _step;
    private readonly AbilityActionOptions _options;

    public EntityAction(IEntityAdapter adapter, EntitySubtype? subtype, EntityActionKind kind, AbilityStep step, AbilityActionOptions options)
    {
        _adapter = adapter;
        _subtype = subtype;
        _kind = kind;
        _step = step;
        _options = options;
        var verb = kind == EntityActionKind.Create ? "create" : "update";
        Alias = $"{options.AliasPrefix}{adapter.EntityType}.{verb}" + (subtype is null ? string.Empty : $".{subtype.Alias}");
        Name = $"{(kind == EntityActionKind.Create ? "Create" : "Update")} {subtype?.Name ?? adapter.DisplayName}";
    }

    public string Alias { get; }

    public string Name { get; }

    public string? Description
    {
        get
        {
            var what = _subtype is null ? _adapter.DisplayName.ToLowerInvariant() : $"{_adapter.DisplayName.ToLowerInvariant()} of type {_subtype.Name}";
            var text = _kind == EntityActionKind.Create
                ? $"Creates a {what} with the values set here, and saves it."
                : $"Changes the values set here on a {what}, and saves it. Values left empty stay as they are.";
            var left = LeftOut();
            return left.Count == 0 ? text : $"{text} Not set here: {string.Join(", ", left)}.";
        }
    }

    // Grouped by entity type in Automate's picker: every document type's actions under "Document".
    public string? Group => _adapter.DisplayName;

    public string? Icon => _kind == EntityActionKind.Create ? "icon-add" : "icon-edit";

    public string? ConnectionTypeAlias => null;

    // Checked when the step runs, as the service account: the adapter's own permission checks.
    public IReadOnlyList<string> RequiredSections => [];

    public IReadOnlyList<string> RequiredPermissions => [];

    // FUDGE (SettingsType): see AbilityAction.
    public Type? SettingsType => typeof(AbilitySettings);

    // Built each time Automate asks, so a field added to the subtype shows up without a restart.
    public EditableModelSchema? GetSettingsSchema()
        => AbilitySettingsForm.Build(JsonSerializer.SerializeToElement(FormSchema(Schema())), _options.FormatEditors);

    public object? ResolveSettings(Dictionary<string, object?> settings) => new AbilitySettings { Values = AbilityStep.ToJson(settings) };

    public Type? OutputType => null;

    // Typed, unlike entity.create's: a later step can pick the key from the editor's list.
    public JsonSchema? GetOutputSchema() => Summary;

    public bool HasDynamicOutputSchema => false;

    public Task<JsonSchema?> GetOutputSchemaAsync(Dictionary<string, object?>? settings, CancellationToken cancellationToken = default)
        => Task.FromResult<JsonSchema?>(Summary);

    public async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var schema = await _adapter.GetSchemaAsync(_subtype?.Alias, cancellationToken);
        var settings = _step.Resolve((context.Settings as AbilitySettings)?.Values ?? [], context, out var bound);

        return await _step.RunAsync(
            context,
            Name,
            writes: true,
            async session =>
            {
                var (key, fields, variant) = await ReadAsync(schema, settings, bound, cancellationToken);
                return _kind == EntityActionKind.Create
                    ? await CreateAsync(session, fields, variant, cancellationToken)
                    : await UpdateAsync(session, key, fields, variant, cancellationToken);
            },
            output: async (call, _) => _kind == EntityActionKind.Create
                ? call.Output
                : await _adapter.LoadAsync((Guid)call.Output!, cancellationToken) is { } entity ? _adapter.ToSummary(entity) : null,
            cancellationToken);
    }

    private async Task<StepCall> CreateAsync(AbilityToolSession session, JsonObject fields, JsonObject? variant, CancellationToken cancellationToken)
        => StepCall.From(await Tool(session, "entity.create").InvokeAsync(
            new JsonObject
            {
                ["entityType"] = _adapter.EntityType,
                ["subtype"] = _subtype?.Alias,
                ["fields"] = fields,
                ["variant"] = variant,
            },
            AbilityConsent.Given,
            cancellationToken));

    // One setValue per value given, all in the step's change set. The output is the key, for the summary after apply.
    private async Task<StepCall> UpdateAsync(AbilityToolSession session, Guid? key, JsonObject fields, JsonObject? variant, CancellationToken cancellationToken)
    {
        if (key is null)
        {
            return StepCall.Fail(AbilityOperationStatus.InvalidInput, $"Pick the {_adapter.DisplayName.ToLowerInvariant()} to update.", [new Problem("key", "Required.")]);
        }

        var setValue = Tool(session, "entity.setValue");
        foreach (var (alias, value) in fields)
        {
            var result = await setValue.InvokeAsync(
                new JsonObject
                {
                    ["entityType"] = _adapter.EntityType,
                    ["key"] = key.ToString(),
                    ["path"] = alias,
                    ["value"] = value?.DeepClone(),
                    ["variant"] = variant?.DeepClone(),
                },
                AbilityConsent.Given,
                cancellationToken);
            if (!result.Success)
            {
                return StepCall.From(result);
            }
        }

        return new StepCall(true, AbilityOperationStatus.Success, key);
    }

    // The step's settings as the key, the field values and the variant. Empty values are left out: on Update they stay
    // as they are. A value set with the product's editor is that editor's value, which the adapter reads as the field's
    // value. A bound value comes from an earlier step's output, so it is taken as the field's value already.
    private async Task<(Guid? Key, JsonObject Fields, JsonObject? Variant)> ReadAsync(
        EntitySchema schema, JsonObject settings, ISet<string> bound, CancellationToken cancellationToken)
    {
        Guid? key = settings["key"] is { } keyValue && Guid.TryParse(KeyOf(keyValue), out var parsed) ? parsed : null;

        JsonObject? variant = null;
        foreach (var dimension in VaryingDimensions(schema))
        {
            if (settings[dimension.Name] is JsonValue chosen && chosen.ToString() is { Length: > 0 } text)
            {
                (variant ??= [])[dimension.Name] = text;
            }
        }

        var fields = new JsonObject();
        var problems = new List<Problem>();
        foreach (var field in FormFields(schema))
        {
            if (IsEmpty(settings[field.Alias]))
            {
                continue;
            }

            var value = settings[field.Alias]!.DeepClone();
            if (field.Editor is not null && !bound.Contains(field.Alias) && _adapter is IEditorValueEntityAdapter editors)
            {
                try
                {
                    value = await editors.FromEditorValueAsync(_subtype?.Alias, field.Alias, value, cancellationToken);
                }
                catch (AbilityInputException ex)
                {
                    problems.AddRange(ex.Problems);
                    continue;
                }
            }

            if (!IsEmpty(value))
            {
                fields[field.Alias] = value;
            }
        }

        return problems.Count > 0
            ? throw new AbilityInputException("Some values could not be read.", problems)
            : (key, fields, variant);
    }

    // The form: the key to update (Update only), the variant to set values in if any field varies, then each field.
    private JsonObject FormSchema(EntitySchema schema)
    {
        var fields = SchemaWriter.Fields(new ObjectSchema(FormFields(schema).ToList()));
        var properties = new JsonObject();
        var required = new JsonArray();
        if (_kind == EntityActionKind.Update)
        {
            properties["key"] = new JsonObject
            {
                ["type"] = "string",
                ["format"] = "uuid",
                ["description"] = $"The {_adapter.DisplayName.ToLowerInvariant()} to update.",
                ["x-references"] = _adapter.EntityType,
            };
            required.Add("key");
        }

        foreach (var dimension in VaryingDimensions(schema))
        {
            var property = new JsonObject { ["type"] = "string", ["description"] = $"{dimension.Description} Values that vary by it are set in this one." };
            if (dimension.Values is { Count: > 0 } values)
            {
                property["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray());
            }

            properties[dimension.Name] = property;
        }

        foreach (var (alias, property) in fields["properties"]!.AsObject().ToList())
        {
            var copy = property!.DeepClone().AsObject();
            if (_kind == EntityActionKind.Update)
            {
                copy["description"] = $"{copy["description"]} Leave empty to keep it as it is.".Trim();
            }

            properties[alias] = copy;
        }

        if (_kind == EntityActionKind.Create)
        {
            foreach (var alias in fields["required"]!.AsArray())
            {
                required.Add(alias?.DeepClone());
            }
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private EntitySchema Schema()
        => _adapter.GetSchemaAsync(_subtype?.Alias).GetAwaiter().GetResult();

    // Fields that hold items aren't in the form. Building them is many edits a fixed form can't describe.
    private static IEnumerable<FieldDefinition> FormFields(EntitySchema schema) => schema.Root.Fields.Where(f => !f.IsItems);

    private List<string> LeftOut() => Schema().Root.Fields.Where(f => f.IsItems).Select(f => f.Alias).ToList();

    // Only the dimensions a root field varies by. Inside blocks, which aren't in the form, don't count.
    private static IEnumerable<VariationDimension> VaryingDimensions(EntitySchema schema)
    {
        var used = FormFields(schema).SelectMany(f => f.VariesBy).ToHashSet(StringComparer.Ordinal);
        return schema.Dimensions.Where(d => used.Contains(d.Name));
    }

    private AbilityTool Tool(AbilityToolSession session, string abilityId)
        => new(_step.Invoker.Abilities.Find(abilityId) ?? throw new InvalidOperationException($"No {abilityId} ability."), _step.Invoker, session);

    // A picker may give the key as text, or as a list of one.
    private static string? KeyOf(JsonNode value) => value switch
    {
        JsonArray { Count: > 0 } list => KeyOf(list[0]!),
        JsonObject picked => (picked["unique"] ?? picked["key"] ?? picked["id"])?.ToString(),
        _ => value.ToString(),
    };

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonValue text when text.GetValueKind() == JsonValueKind.String => text.GetValue<string>().Length == 0,
        JsonArray list => list.Count == 0,
        _ => false,
    };
}
