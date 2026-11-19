using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Consumers;

namespace Umbraco.Abilities.Entities;

// The generic entity abilities. They work for every registered entity adapter, so no package writes
// create_discount, add_discount_rule and so on. Edits only run inside a change set.

public interface IEntityEditInput
{
    string EntityType { get; }

    Guid? Key { get; }

    /// <summary>
    /// The variant to edit, e.g. <c>{ "culture": "da-DK" }</c>. Values that don't vary ignore it. Inputs for edits that
    /// don't touch values by variant (a delete, a move) leave it out.
    /// </summary>
    Dictionary<string, string>? Variant => null;
}

/// <summary>Base for entity edits that return plain JSON. See <see cref="EntityEditAbilityBase{TInput, TOutput}"/>.</summary>
public abstract class EntityEditAbilityBase<TInput> : EntityEditAbilityBase<TInput, object>
    where TInput : IEntityEditInput;

/// <summary>Base for entity edits: recorded into a change set, applied to a working copy on replay.</summary>
public abstract class EntityEditAbilityBase<TInput, TOutput> : AbilityBase<TInput, TOutput>, IEntityEditAbility
    where TInput : IEntityEditInput
{
    protected virtual bool Creates => false;

    public EntityEditTarget Prepare(JsonObject input)
    {
        FillKeys(input);
        var typed = Read(input);
        if (string.IsNullOrWhiteSpace(typed.EntityType))
        {
            throw new AbilityInputException("entityType is required.", [new Problem(EntityJson.EntityType, "Required.")]);
        }

        var key = typed.Key ?? throw new AbilityInputException("key is required.", [new Problem(EntityJson.Key, "Required.")]);
        return new EntityEditTarget(typed.EntityType, key, Creates, (typed as CreateEntityInput)?.Subtype);
    }

    public Task<object?> ApplyAsync(Entity entity, JsonObject input, EntityEditContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(Apply(entity, Read(input), context));

    /// <summary>Writes new keys into the recorded input so replay always produces the same result.</summary>
    protected virtual void FillKeys(JsonObject input)
    {
    }

    protected abstract object? Apply(Entity entity, TInput input, EntityEditContext context);

    protected override Task<AbilityResult> ExecuteAsync(TInput input, AbilityContext context, CancellationToken cancellationToken)
        => Task.FromResult(AbilityResult.Fail(
            AbilityOperationStatus.InvalidInput,
            "Entity edits are recorded in a change set, and none is open. Create one with changeSet_create, or name one with changeSetId.",
            [new Problem(AbilityTool.ChangeSetIdArgument, "No change set is open.")]));

    private static TInput Read(JsonObject input)
        => input.Deserialize<TInput>(AbilityJson.Options) ?? throw new AbilityInputException("Input is required.");

    protected static EditTarget Target(Entity entity, TInput input, EntityEditContext context)
        => new(entity, context.Schema, context.ItemTypes, new VariantId(input.Variant?.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))));

    protected static void EnsureKey(JsonObject input, string property)
    {
        if (input[property] is null || string.IsNullOrWhiteSpace(input[property]!.ToString()))
        {
            input[property] = Guid.NewGuid().ToString();
        }
    }
}

public sealed record GetEntitySchemaInput(
    [property: Description("Entity type, e.g. 'umbraco-commerce-discount' (a UDI entity type).")] string EntityType,
    [property: Description("Optional item type aliases to get the full schema for.")]
    [property: Hint(AbilityAudiences.Model, "From an items field's x-item-types. Use group:alias when an alias appears in several groups.")] string[]? ItemTypes = null,
    [property: Description("The subtype, for entity types that have them, e.g. a document's content type alias.")] string? Subtype = null);

[Ability("entity.getSchema", "Get entity schema", Kind = AbilityKind.Read, UsesChangeSet = false,
    Description = "Returns an entity type's fields as JSON Schema.")]
[Hint(AbilityAudiences.Model, "An items field lists its item types (x-item-types). " +
                  "Call it first. Call again with itemTypes to get the fields of the item types you will add. " +
                  "capabilities lists what the type supports beyond setting values, such as delete and move. " +
                  "A field with enum takes one of those values as written: it is not a reference, so don't look it up. " +
                  "Only a field with x-references points at another entity.")]
public sealed class GetEntitySchemaAbility(EntityAdapterRegistry adapters, ItemTypeRegistry registry)
    : AbilityBase<GetEntitySchemaInput, JsonObject>
{
    protected override async Task<AbilityResult> ExecuteAsync(GetEntitySchemaInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var adapter = adapters.Get(input.EntityType);
        var subtypes = await adapter.GetSubtypesAsync(cancellationToken);
        if (subtypes.Count > 0 && input.Subtype is null)
        {
            return Success(new JsonObject
            {
                [EntityJson.EntityType] = input.EntityType,
                ["subtypes"] = new JsonArray(subtypes.Select(s => (JsonNode?)new JsonObject { ["alias"] = s.Alias, ["name"] = s.Name, ["creatable"] = s.Creatable }).ToArray()),
                ["next"] = "Call again with a subtype to get its fields.",
            });
        }

        var schema = await adapter.GetSchemaAsync(input.Subtype, cancellationToken);
        var itemTypes = await registry.ResolveAsync(schema.Root, context.UserKey, cancellationToken);
        var output = new JsonObject
        {
            [EntityJson.EntityType] = schema.EntityType,
            ["name"] = schema.Name,
            ["description"] = schema.Description,
            ["dimensions"] = new JsonArray(schema.Dimensions.Select(d => (JsonNode?)new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = d.Description,
                ["values"] = d.Values is { } values ? new JsonArray(values.Select(v => (JsonNode?)v).ToArray()) : null,
            }).ToArray()),
            [EntityJson.Fields] = SchemaWriter.Fields(schema.Root, itemTypes, context.UserKey),
            ["capabilities"] = new JsonArray(Capabilities(adapter).Select(c => (JsonNode?)c).ToArray()),
        };

        if (input.ItemTypes is { Length: > 0 })
        {
            var details = new JsonObject();
            foreach (var alias in input.ItemTypes)
            {
                var itemType = itemTypes.Find(alias) is { } found && itemTypes.IsAvailable(found) ? found
                    : throw new AbilityInputException($"Unknown item type '{alias}'.", [new Problem("itemTypes", $"Unknown: {alias}.")]);
                details[alias] = new JsonObject
                {
                    ["name"] = itemType.Name,
                    [EntityJson.Fields] = SchemaWriter.Fields(itemType.Schema, itemTypes, context.UserKey),
                };
            }

            output["itemTypes"] = details;
        }

        return Success(output);
    }

    /// <summary>What an entity type can do beyond setting values: the optional adapter interfaces it implements.</summary>
    private static IEnumerable<string> Capabilities(IEntityAdapter adapter)
    {
        if (adapter is IDeletableEntityAdapter)
        {
            yield return "delete";
        }

        if (adapter is ITreeEntityAdapter)
        {
            yield return "move";
        }
    }
}

public sealed record FindEntityInput(
    [property: Description("Entity type to search, e.g. 'umbraco-commerce-collection'.")] string EntityType,
    [property: Description("Words in the entity's name. Leave it empty to list them all, for example every one of a subtype.")] string Query = "",
    [property: Description("Only entities of this subtype, e.g. a document's content type alias.")] string? Subtype = null);

[Ability("entity.find", "Find entities", Kind = AbilityKind.Read,
    Description = "Finds entities by name, live and in the active change set.")]
[Hint(AbilityAudiences.Model, "Use it to turn a name the person used into a key, " +
                  "for a field with x-references. Each result has its subtype. A field with enum needs no lookup.")]
public sealed class FindEntityAbility(EntityAdapterRegistry adapters, ChangeSetService changeSets)
    : AbilityBase<FindEntityInput, IReadOnlyList<EntitySummary>>
{
    protected override async Task<AbilityResult> ExecuteAsync(FindEntityInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var adapter = adapters.Get(input.EntityType);
        var found = (await adapter.FindAsync(input.Query ?? string.Empty, cancellationToken)).ToList();

        if (changeSets.CurrentId is { } setId)
        {
            // Entities the set touches are matched on their working state, not their live name.
            var set = changeSets.Get(setId);
            var touched = set.Changes.Where(c => c.EntityType == input.EntityType).Select(c => c.EntityKey!.Value).Distinct().ToList();
            found.RemoveAll(r => touched.Contains(r.Key));

            foreach (var key in touched)
            {
                // An entity the set deletes isn't found.
                if (await changeSets.GetWorkingStateAsync(set, input.EntityType, key, cancellationToken) is not { } entity)
                {
                    continue;
                }

                var reference = adapter.ToSummary(entity);
                if (reference.Name.Contains(input.Query ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(reference);
                }
            }
        }

        if (input.Subtype is { Length: > 0 } subtype)
        {
            found.RemoveAll(r => !string.Equals(r.Subtype, subtype, StringComparison.OrdinalIgnoreCase));
        }

        // Only what the user may read. (What the set itself touches was checked when it was recorded.)
        var readable = new List<EntitySummary>();
        var inSet = changeSets.CurrentId is { } id ? changeSets.Get(id).Changes.Where(c => c.EntityType == input.EntityType).Select(c => c.EntityKey).ToHashSet() : [];
        foreach (var reference in found)
        {
            if (inSet.Contains(reference.Key)
                || await EntityAccess.CheckAsync(adapter, new EntityAccessRequest(context.UserKey, EntityPermission.Read, reference.Key, null, new HashSet<VariantId>()), cancellationToken) is null)
            {
                readable.Add(reference);
            }
        }

        return Success(readable);
    }
}

public sealed record GetEntityInput(
    [property: Description("Entity type.")] string EntityType,
    [property: Description("Entity key.")] Guid Key);

[Ability("entity.get", "Get entity", Kind = AbilityKind.Read,
    Description = "Returns an entity's fields, items included. Inside a change set this includes the set's edits.")]
public sealed class GetEntityAbility(EntityAdapterRegistry adapters, ChangeSetService changeSets)
    : AbilityBase<GetEntityInput, JsonObject>
{
    protected override async Task<AbilityResult> ExecuteAsync(GetEntityInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var adapter = adapters.Get(input.EntityType);

        // What the current set touches was checked when it was recorded, as in entity.find. A new entity exists only in
        // the set, so the product couldn't check it anyway: it would say it doesn't exist.
        var inSet = changeSets.CurrentId is { } id
            && changeSets.Get(id).Changes.Any(c => c.EntityType == input.EntityType && c.EntityKey == input.Key);
        if (!inSet
            && await EntityAccess.CheckAsync(adapter, new EntityAccessRequest(context.UserKey, EntityPermission.Read, input.Key, null, new HashSet<VariantId>()), cancellationToken) is { } denied)
        {
            return EntityAccess.Denied(denied, EntityJson.Key);
        }

        var entity = await changeSets.ReadAsync(adapter, input.Key, cancellationToken);

        return entity is null
            ? AbilityResult.Fail(AbilityOperationStatus.InvalidInput, $"No {input.EntityType} with key {input.Key}.", [new Problem(EntityJson.Key, "Not found.")])
            : Success(entity.ToJson());
    }
}

public sealed record CreateEntityInput(
    [property: Description("Entity type to create.")] string EntityType,
    [property: Description("The subtype to create, for entity types that have them, e.g. a document's content type alias.")] string? Subtype = null,
    [property: Description("Field values, keyed by field alias.")] JsonObject? Fields = null,
    [property: Description("The variant to edit, by dimension, e.g. { \"culture\": \"en-US\" }.")]
    [property: Hint(AbilityAudiences.Model, "Needed for values that vary (x-variations); values that don't vary ignore it. Leave out an open dimension, such as a segment, for its default.")] Dictionary<string, string>? Variant = null,
    [property: Description("Optional key for the new entity. Leave it out and one is made.")]
    [property: Hint(AbilityAudiences.Model, "Choose one to refer to the entity from others in the same change set. The key is returned either way.")]
    [property: Hint(AbilityAudiences.Model, "Choose one only when other entities in the set must refer to it, and make it a new random GUID, not a pattern.")] Guid? Key = null) : IEntityEditInput;

[Ability("entity.create", "Create entity", Kind = AbilityKind.Write, Reversible = true,
    Description = "Creates an entity in the active change set. Nothing goes live until the set is applied. Returns the new key.",
    SplitBy = EntityJson.EntityType, SplitTitle = "Create {0}", DependentInput = EntityJson.Fields, DependsOn = [EntityJson.EntityType, EntityJson.Subtype])]
[Hint(AbilityAudiences.Model, "Add items to items fields afterwards with entity.addItem.")]
public sealed class CreateEntityAbility(EntityAdapterRegistry adapters) : EntityEditAbilityBase<CreateEntityInput, EntitySummary>, IDynamicInputAbility
{
    protected override bool Creates => true;

    protected override void FillKeys(JsonObject input) => EnsureKey(input, EntityJson.Key);

    protected override object? Apply(Entity entity, CreateEntityInput input, EntityEditContext context)
    {
        var target = Target(entity, input, context);
        EntityEditor.SetFields(target, entity.Fields, context.Schema.Root, string.Empty, input.Fields);
        EntityEditor.FillDefaults(target, entity.Fields, context.Schema.Root, string.Empty);
        return context.Adapter.ToSummary(entity);
    }

    public Task<IReadOnlyList<AbilityChoice>> GetChoicesAsync(string argument, JsonObject chosen, CancellationToken cancellationToken = default)
        => EntityInputs.ChoicesAsync(adapters, argument, chosen, creating: true, cancellationToken);

    public Task<JsonElement> ResolveInputSchemaAsync(JsonObject chosen, CancellationToken cancellationToken = default)
        => EntityInputs.ResolveAsync(adapters, typeof(CreateEntityInput), chosen, creating: true, cancellationToken);

    public Task<JsonObject> FromEditorValuesAsync(JsonObject chosen, JsonObject values, ISet<string> bound, CancellationToken cancellationToken = default)
        => EntityInputs.FromEditorValuesAsync(adapters, chosen, values, bound, cancellationToken);
}

public sealed record UpdateEntityInput(
    [property: Description("Entity type to update.")] string EntityType,
    [property: Description("Key of the entity to update.")] Guid? Key,
    [property: Description("The entity's subtype, e.g. a document's content type alias. Optional: it picks the fields to show, and is checked against the entity's own.")] string? Subtype = null,
    [property: Description("The values to change, keyed by field alias. Fields left out stay as they are.")] JsonObject? Fields = null,
    [property: Description("The variant to edit, by dimension, e.g. { \"culture\": \"en-US\" }.")]
    [property: Hint(AbilityAudiences.Model, "Needed for values that vary (x-variations); values that don't vary ignore it. Leave out an open dimension, such as a segment, for its default.")] Dictionary<string, string>? Variant = null) : IEntityEditInput;

[Ability("entity.update", "Update entity", Kind = AbilityKind.Write, Reversible = true,
    Description = "Changes some of an entity's values at once, in the active change set. Values left out stay as they are.",
    SplitBy = EntityJson.EntityType, SplitTitle = "Update {0}", DependentInput = EntityJson.Fields, DependsOn = [EntityJson.EntityType, EntityJson.Subtype])]
[Hint(AbilityAudiences.Model, "Takes top-level fields only. Use entity.setValue for a value inside items, and entity.addItem to add items.")]
public sealed class UpdateEntityAbility(EntityAdapterRegistry adapters) : EntityEditAbilityBase<UpdateEntityInput, EntitySummary>, IDynamicInputAbility
{
    protected override object? Apply(Entity entity, UpdateEntityInput input, EntityEditContext context)
    {
        if (input.Subtype is { } subtype && !string.Equals(subtype, entity.Subtype, StringComparison.Ordinal))
        {
            throw new AbilityInputException(
                $"{context.Adapter.DisplayName} {entity.Key} is a {entity.Subtype}, not a {subtype}.",
                [new Problem(EntityJson.Subtype, $"Is {entity.Subtype}.")]);
        }

        var target = Target(entity, input, context);
        foreach (var (alias, value) in input.Fields ?? [])
        {
            EntityEditor.SetValue(target, alias, value?.DeepClone());
        }

        return context.Adapter.ToSummary(entity);
    }

    public Task<IReadOnlyList<AbilityChoice>> GetChoicesAsync(string argument, JsonObject chosen, CancellationToken cancellationToken = default)
        => EntityInputs.ChoicesAsync(adapters, argument, chosen, creating: false, cancellationToken);

    public Task<JsonElement> ResolveInputSchemaAsync(JsonObject chosen, CancellationToken cancellationToken = default)
        => EntityInputs.ResolveAsync(adapters, typeof(UpdateEntityInput), chosen, creating: false, cancellationToken);

    public Task<JsonObject> FromEditorValuesAsync(JsonObject chosen, JsonObject values, ISet<string> bound, CancellationToken cancellationToken = default)
        => EntityInputs.FromEditorValuesAsync(adapters, chosen, values, bound, cancellationToken);
}

/// <summary>
/// What <c>entity.create</c> and <c>entity.update</c> share as dynamic inputs: the entity types and subtypes to choose
/// from, and the chosen subtype's fields as the <c>fields</c> input's schema.
/// </summary>
internal static class EntityInputs
{
    private const string KeepHint = "Leave it empty to keep it as it is.";

    public static async Task<IReadOnlyList<AbilityChoice>> ChoicesAsync(
        EntityAdapterRegistry adapters, string argument, JsonObject chosen, bool creating, CancellationToken cancellationToken)
    {
        switch (argument)
        {
            case EntityJson.EntityType:
                return adapters.All.Where(a => a.CanCreate).Select(a => new AbilityChoice(a.EntityType, a.DisplayName)).ToList();
            case EntityJson.Subtype when Text(chosen, EntityJson.EntityType) is { } entityType && adapters.Find(entityType) is { } adapter:
                var subtypes = await adapter.GetSubtypesAsync(cancellationToken);
                return subtypes.Where(s => s.Creatable || !creating).Select(s => new AbilityChoice(s.Alias, s.Name)).ToList();
            default:
                return [];
        }
    }

    public static async Task<JsonElement> ResolveAsync(
        EntityAdapterRegistry adapters, Type inputType, JsonObject chosen, bool creating, CancellationToken cancellationToken)
    {
        var schema = JsonNode.Parse(AbilitySchemas.InputSchema(inputType).GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        OneOf(properties, EntityJson.EntityType, await ChoicesAsync(adapters, EntityJson.EntityType, chosen, creating, cancellationToken));

        if (Text(chosen, EntityJson.EntityType) is not { } entityType || adapters.Find(entityType) is not { } adapter)
        {
            return JsonSerializer.SerializeToElement(schema);
        }

        if (!creating && properties[EntityJson.Key] is JsonObject key)
        {
            // The entity to update, picked from that type's entities.
            key[SchemaKeywords.References] = entityType;
            if (schema["required"] is not JsonArray required)
            {
                schema["required"] = required = [];
            }

            if (!required.Any(r => r?.GetValue<string>() == EntityJson.Key))
            {
                required.Add(EntityJson.Key);
            }
        }

        var subtypes = await ChoicesAsync(adapters, EntityJson.Subtype, chosen, creating, cancellationToken);
        var subtype = Text(chosen, EntityJson.Subtype);
        if (subtypes.Count > 0)
        {
            OneOf(properties, EntityJson.Subtype, subtypes);
            if (subtype is null)
            {
                return JsonSerializer.SerializeToElement(schema);
            }
        }
        else
        {
            properties.Remove(EntityJson.Subtype);
        }

        var entity = await adapter.GetSchemaAsync(subtype, cancellationToken);
        var fields = SchemaWriter.Fields(entity.Root);
        fields["description"] = properties[EntityJson.Fields]?["description"]?.DeepClone();
        if (!creating)
        {
            // An update changes only what is given: nothing is required, and a person is told an empty value stays.
            fields["required"] = new JsonArray();
            foreach (var (_, field) in fields["properties"]!.AsObject())
            {
                var hints = UiHints.WithHint(field![UiHints.HintsKeyword]?.Deserialize<Dictionary<string, string>>(), AbilityAudiences.Person, KeepHint);
                field[UiHints.HintsKeyword] = JsonSerializer.SerializeToNode(hints);
            }
        }

        properties[EntityJson.Fields] = fields;

        // The variant, as one choice per dimension a field varies by.
        var used = entity.Root.Fields.SelectMany(f => f.VariesBy).ToHashSet(StringComparer.Ordinal);
        if (properties["variant"] is JsonObject variant)
        {
            var dimensions = new JsonObject();
            foreach (var dimension in entity.Dimensions.Where(d => used.Contains(d.Name)))
            {
                var property = new JsonObject { ["type"] = "string", ["description"] = dimension.Description };
                if (dimension.Values is { Count: > 0 } values)
                {
                    property["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray());
                }

                dimensions[dimension.Name] = property;
            }

            if (dimensions.Count == 0)
            {
                properties.Remove("variant");
            }
            else
            {
                variant.Remove("additionalProperties");
                variant["type"] = "object";
                variant["properties"] = dimensions;
            }
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    public static async Task<JsonObject> FromEditorValuesAsync(
        EntityAdapterRegistry adapters, JsonObject chosen, JsonObject values, ISet<string> bound, CancellationToken cancellationToken)
    {
        if (Text(chosen, EntityJson.EntityType) is not { } entityType || adapters.Find(entityType) is not IEditorValueEntityAdapter editors)
        {
            return values;
        }

        var subtype = Text(chosen, EntityJson.Subtype);
        var schema = await editors.GetSchemaAsync(subtype, cancellationToken);
        var read = new JsonObject();
        var problems = new List<Problem>();
        foreach (var (alias, value) in values)
        {
            if (value is null || bound.Contains(alias) || schema.Root.Fields.FirstOrDefault(f => f.Alias == alias) is not { Editor: not null })
            {
                read[alias] = value?.DeepClone();
                continue;
            }

            try
            {
                read[alias] = await editors.FromEditorValueAsync(subtype, alias, value.DeepClone(), cancellationToken);
            }
            catch (AbilityInputException ex)
            {
                problems.AddRange(ex.Problems);
            }
        }

        return problems.Count > 0 ? throw new AbilityInputException("Some values could not be read.", problems) : read;
    }

    private static string? Text(JsonObject chosen, string name)
        => chosen[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text ? text : null;

    // The choices as JSON Schema's labelled list: each value a const, with its name as the title.
    private static void OneOf(JsonObject properties, string name, IReadOnlyList<AbilityChoice> choices)
    {
        if (properties[name] is not JsonObject property || choices.Count == 0)
        {
            return;
        }

        property["oneOf"] = new JsonArray(choices.Select(c => (JsonNode?)new JsonObject { ["const"] = c.Value, ["title"] = c.Name }).ToArray());
    }
}

public sealed record SetEntityValueInput(
    [property: Description("Entity type.")] string EntityType,
    [property: Description("Entity key.")] Guid? Key,
    [property: Description("Path of the value: a field alias, going down through items by key and objects by field, e.g. \"title\", \"blocks/{itemKey}/heading\" or \"blocks/{itemKey}/settings/theme\".")] string Path,
    [property: Description("The new value, matching the field's schema. null clears it.")] JsonNode? Value,
    [property: Description("The variant to edit, by dimension, e.g. { \"culture\": \"en-US\" }.")]
    [property: Hint(AbilityAudiences.Model, "Needed for values that vary (x-variations); values that don't vary ignore it. Leave out an open dimension, such as a segment, for its default.")] Dictionary<string, string>? Variant = null) : IEntityEditInput;

[Ability("entity.setValue", "Set entity value", Kind = AbilityKind.Write, Reversible = true, Granular = true,
    Description = "Sets the value at a path, in the active change set.")]
[Hint(AbilityAudiences.Model, "A path goes down through items by key and objects by " +
                  "field. Give a culture for values that vary by culture. Problem paths from changeSet.validate have the same form.")]
public sealed class SetEntityValueAbility : EntityEditAbilityBase<SetEntityValueInput>
{
    protected override object? Apply(Entity entity, SetEntityValueInput input, EntityEditContext context)
    {
        return new { path = EntityEditor.SetValue(Target(entity, input, context), input.Path, input.Value) };
    }
}

public sealed record AddEntityItemInput(
    [property: Description("Entity type.")] string EntityType,
    [property: Description("Entity key.")] Guid? Key,
    [property: Description("Path of the items field, e.g. \"rules\", or \"blocks/{itemKey}/cards\" for one inside an item.")] string FieldPath,
    [property: Description("Item type alias.")]
    [property: Hint(AbilityAudiences.Model, "From the field's x-item-types.")] string ItemType,
    [property: Description("Optional key for the new item. Leave it out and one is made and returned.")]
    [property: Hint(AbilityAudiences.Model, "Choose one only when another item must refer to it, and make it a new random GUID, not a pattern.")] Guid? ItemKey = null,
    [property: Description("Field values for the new item, keyed by field alias.")] JsonObject? Fields = null,
    [property: Description("Optional zero-based position. Omit to append.")] int? Position = null,
    [property: Description("The variant to edit, by dimension, e.g. { \"culture\": \"en-US\" }.")]
    [property: Hint(AbilityAudiences.Model, "Needed for values that vary (x-variations); values that don't vary ignore it. Leave out an open dimension, such as a segment, for its default.")] Dictionary<string, string>? Variant = null) : IEntityEditInput;

[Ability("entity.addItem", "Add entity item", Kind = AbilityKind.Write, Reversible = true, Granular = true,
    Description = "Adds a typed item to an items field, in the active change set.")]
[Hint(AbilityAudiences.Model, "Returns the item's path; set its fields " +
                  "at '<item path>/<field>', and use '<item path>/<items field>' as fieldPath to add items inside it.")]
public sealed class AddEntityItemAbility : EntityEditAbilityBase<AddEntityItemInput>
{
    protected override void FillKeys(JsonObject input) => EnsureKey(input, "itemKey");

    protected override object? Apply(Entity entity, AddEntityItemInput input, EntityEditContext context)
    {
        var path = EntityEditor.AddItem(Target(entity, input, context), input.FieldPath, input.ItemType, input.ItemKey!.Value, input.Fields, input.Position);
        return new { itemKey = input.ItemKey, path };
    }
}

public sealed record RemoveEntityItemInput(
    [property: Description("Entity type.")] string EntityType,
    [property: Description("Entity key.")] Guid? Key,
    [property: Description("Path of the items field.")] string FieldPath,
    [property: Description("Key of the item to remove.")] Guid ItemKey,
    [property: Description("The variant to edit, by dimension, e.g. { \"culture\": \"en-US\" }.")]
    [property: Hint(AbilityAudiences.Model, "Needed for values that vary (x-variations); values that don't vary ignore it. Leave out an open dimension, such as a segment, for its default.")] Dictionary<string, string>? Variant = null) : IEntityEditInput;

[Ability("entity.removeItem", "Remove entity item", Kind = AbilityKind.Write, Reversible = true, Granular = true,
    Description = "Removes an item from an items field, in the active change set.")]
public sealed class RemoveEntityItemAbility : EntityEditAbilityBase<RemoveEntityItemInput>
{
    protected override object? Apply(Entity entity, RemoveEntityItemInput input, EntityEditContext context)
    {
        EntityEditor.RemoveItem(Target(entity, input, context), input.FieldPath, input.ItemKey);
        return new { removed = input.ItemKey };
    }
}

public sealed record MoveEntityItemInput(
    [property: Description("Entity type.")] string EntityType,
    [property: Description("Entity key.")] Guid? Key,
    [property: Description("Path of the items field.")] string FieldPath,
    [property: Description("Key of the item to move.")] Guid ItemKey,
    [property: Description("New zero-based position.")] int Position,
    [property: Description("The variant to edit, by dimension, e.g. { \"culture\": \"en-US\" }.")]
    [property: Hint(AbilityAudiences.Model, "Needed for values that vary (x-variations); values that don't vary ignore it. Leave out an open dimension, such as a segment, for its default.")] Dictionary<string, string>? Variant = null) : IEntityEditInput;

[Ability("entity.moveItem", "Move entity item", Kind = AbilityKind.Write, Reversible = true, Granular = true,
    Description = "Moves an item within its items field, in the active change set.")]
public sealed class MoveEntityItemAbility : EntityEditAbilityBase<MoveEntityItemInput>
{
    protected override object? Apply(Entity entity, MoveEntityItemInput input, EntityEditContext context)
    {
        EntityEditor.MoveItem(Target(entity, input, context), input.FieldPath, input.ItemKey, input.Position);
        return new { moved = input.ItemKey, position = input.Position };
    }
}

public sealed record DeleteEntityInput(
    [property: Description("Entity type.")] string EntityType,
    [property: Description("Key of the entity to delete.")] Guid? Key) : IEntityEditInput;

[Ability("entity.delete", "Delete entity", Kind = AbilityKind.Write, Reversible = true,
    Description = "Deletes an entity, in the active change set. It stays live until the set is applied; inside the set it reads as gone.")]
[Hint(AbilityAudiences.Model, "Only for entity types whose capabilities (from entity.getSchema) include delete.")]
public sealed class DeleteEntityAbility : EntityEditAbilityBase<DeleteEntityInput>
{
    protected override object? Apply(Entity entity, DeleteEntityInput input, EntityEditContext context)
    {
        if (context.Adapter is not IDeletableEntityAdapter deletable)
        {
            throw new AbilityInputException(
                $"{context.Adapter.DisplayName} can't be deleted here. entity.getSchema lists what an entity type supports under capabilities.",
                [new Problem(EntityJson.EntityType, "Can't be deleted.")]);
        }

        // Marked, not deleted: the change set reads it as gone, and deletes it when the set is applied.
        entity.Deleted = true;
        return new { deleted = entity.Key, canRestore = deletable.CanRestore };
    }
}

public sealed record MoveEntityInput(
    [property: Description("Entity type.")] string EntityType,
    [property: Description("Key of the entity to move.")] Guid? Key,
    [property: Description("Key of the new parent. Leave it out to move the entity to the root.")] Guid? Parent = null) : IEntityEditInput;

[Ability("entity.move", "Move entity", Kind = AbilityKind.Write, Reversible = true,
    Description = "Moves an entity to another parent in its tree, in the active change set. Leave parent out for the root.")]
[Hint(AbilityAudiences.Model, "Only for entity types whose capabilities (from entity.getSchema) include move.")]
public sealed class MoveEntityAbility : EntityEditAbilityBase<MoveEntityInput>
{
    protected override object? Apply(Entity entity, MoveEntityInput input, EntityEditContext context)
    {
        if (context.Adapter is not ITreeEntityAdapter tree)
        {
            throw new AbilityInputException(
                $"{context.Adapter.DisplayName} can't be moved. entity.getSchema lists what an entity type supports under capabilities.",
                [new Problem(EntityJson.EntityType, "Can't be moved.")]);
        }

        if (input.Parent == entity.Key)
        {
            throw new AbilityInputException("An entity can't be its own parent.", [new Problem("parent", "Can't be the entity itself.")]);
        }

        // Set like any value, so the reference is checked against the field's schema, and rebasing and review see a value.
        EntityEditor.SetValue(Target(entity, input, context), tree.ParentField, input.Parent is { } parent ? JsonValue.Create(parent.ToString()) : null);
        return new { moved = entity.Key, parent = input.Parent };
    }
}
