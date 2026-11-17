using System.Text.Json.Nodes;

namespace Umbraco.Abilities.Entities;

/// <summary>
/// Checks a whole entity against its schema: required fields, value types, item counts, allowed item
/// types and references. Values that vary are checked for every variant the entity has, and problems say
/// which culture and segment they are in. Domain rules stay in <see cref="IEntityAdapter.ValidateAsync"/>.
/// </summary>
public static class SchemaValidator
{
    private sealed record Context(
        Entity Entity,
        EntitySchema Schema,
        ItemTypes ItemTypes,
        Func<string, Guid, Task<bool>> EntityExists,
        List<Problem> Problems);

    /// <param name="entityExists">Whether an entity is live or created in the same change set.</param>
    public static async Task<IReadOnlyList<Problem>> ValidateAsync(
        Entity entity,
        EntitySchema schema,
        ItemTypes itemTypes,
        Func<string, Guid, Task<bool>> entityExists)
    {
        var problems = new List<Problem>();
        var variants = EntityValues.VariantsIn(entity.Fields, schema.Root, itemTypes);

        foreach (var variant in variants)
        {
            foreach (var (name, value) in variant.Values)
            {
                if (schema.Dimension(name) is not { } dimension || dimension.Values is { } values && !values.Contains(value))
                {
                    problems.Add(new Problem(string.Empty, $"Unknown {name} '{value}'.").In(variant));
                }
            }
        }

        var context = new Context(entity, schema, itemTypes, entityExists, problems);
        await ValidateObjectAsync(context, entity.Fields, schema.Root, string.Empty, variants);
        return problems;
    }

    /// <param name="variants">The variants the object exists in. Below a value that varies, only that value's variant.</param>
    private static async Task ValidateObjectAsync(Context context, JsonObject owner, ObjectSchema schema, string path, IReadOnlyList<VariantId> variants)
    {
        var existsIn = InOpenValuesOfItsOwn(context.Schema, owner, schema, variants);
        foreach (var field in schema.Fields)
        {
            var fieldPath = EntityValues.Join(path, field.Alias);
            var targets = Targets(context.Schema, field.VariesBy, existsIn).ToList();
            if (field.Required && field.Varies && targets.Count == 0)
            {
                // A required value that varies, with no value in any variant yet: the entity has no culture (or currency…) yet.
                context.Problems.Add(new Problem(fieldPath, $"Required. Give it a value in at least one {string.Join(" and ", field.VariesBy)}."));
            }

            foreach (var variant in targets)
            {
                var value = EntityValues.Get(owner, field, variant);
                var below = field.Varies ? variants.Where(variant.Within).ToList() : variants;
                switch (field.Type)
                {
                    case FieldType.Items:
                        await ValidateItemsAsync(context, field, value as JsonArray ?? [], fieldPath, variant, below);
                        break;
                    case FieldType.Object when field.Schema is { } objectSchema && value is JsonObject obj:
                        await ValidateObjectAsync(context, obj, objectSchema, fieldPath, below);
                        AddHandlerProblems(context, field, obj, fieldPath, variant);
                        break;
                    default:
                        await ValidateFieldAsync(context, field, value, fieldPath, variant);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The variants an object's own values are checked in. A value of an open dimension (a CMS segment) counts only
    /// where the object has values of its own in it, in that variant's other dimensions: a VIP value inside a block
    /// doesn't make the page need a VIP heading, and a Danish VIP value doesn't make English need one. This is how
    /// Umbraco validates segments (release-18.0.0: ContentValidationServiceBase.GetPopulatedSegmentCultures).
    /// Values below still see every variant, so each value stored is checked.
    /// </summary>
    private static IReadOnlyList<VariantId> InOpenValuesOfItsOwn(EntitySchema entitySchema, JsonObject owner, ObjectSchema schema, IReadOnlyList<VariantId> variants)
    {
        var open = entitySchema.Dimensions.Where(d => d.Values is null).Select(d => d.Name).ToList();
        if (open.Count == 0 || variants.All(v => open.All(d => v[d] is null)))
        {
            return variants;
        }

        var own = EntityValues.OwnVariants(owner, schema);
        return variants
            .Where(v => open.All(d => v[d] is null) || own.Any(o => o.Within(v) && open.All(d => v[d] is null || o[d] == v[d])))
            .ToList();
    }

    private static async Task ValidateItemsAsync(Context context, FieldDefinition field, JsonArray items, string fieldPath, VariantId scope, IReadOnlyList<VariantId> variants)
    {
        var located = new Problem(fieldPath, string.Empty).In(scope);
        if (field.Min is { } min && items.Count < min)
        {
            context.Problems.Add(located with { ErrorMessages = [$"Needs at least {min} item(s)."] });
        }

        if (field.Max is { } max && items.Count > max)
        {
            context.Problems.Add(located with { ErrorMessages = [$"Holds at most {max} item(s)."] });
        }

        var allowed = context.ItemTypes.InGroup(field.ItemTypeGroup!).ToDictionary(t => t.Alias);
        foreach (var item in items.OfType<JsonObject>())
        {
            var itemPath = EntityValues.Join(fieldPath, EntityValues.KeyOf(item).ToString());
            if (!allowed.TryGetValue(EntityValues.TypeOf(item), out var itemType))
            {
                context.Problems.Add(located with { Path = itemPath, ErrorMessages = [$"Item type '{EntityValues.TypeOf(item)}' is not allowed here."] });
                continue;
            }

            // An item exists in the variants it has values in, or in every variant when it has none yet.
            // This is the CMS's "expose": a block with only English values is not in the Danish page.
            var own = EntityValues.OwnVariants(item, itemType.Schema);
            var itemVariants = own.Count == 0 ? variants : variants.Where(v => v.IsInvariant || own.Any(o => o.Within(v))).ToList();
            await ValidateObjectAsync(context, item, itemType.Schema, itemPath, itemVariants);
        }
    }

    private static void AddHandlerProblems(Context context, FieldDefinition field, JsonObject value, string path, VariantId variant)
    {
        if (field.Handler is { } handler)
        {
            context.Problems.AddRange(handler.Validate(value).Select(p => p with
            {
                Path = EntityValues.Join(path, p.Path),
                Variant = p.Variant ?? variant.ToJson(),
            }));
        }
    }

    private static async Task ValidateFieldAsync(Context context, FieldDefinition field, JsonNode? value, string path, VariantId variant)
    {
        Problem At(string message) => new Problem(path, message).In(variant);

        if (value is null)
        {
            if (field.Required)
            {
                context.Problems.Add(At("Required."));
            }

            return;
        }

        if (FieldChecker.IsBinding(field, value))
        {
            return; // Checked when the binding resolves.
        }

        if (FieldChecker.Check(field, value, path) is { } problem)
        {
            context.Problems.Add(At(problem.ToString()));
            return;
        }

        foreach (var message in FieldChecker.CheckRules(field, value))
        {
            context.Problems.Add(At(message));
        }

        if (field.Type == FieldType.EntityReference)
        {
            foreach (var (entityType, key) in FieldChecker.EntityReferences(field, value))
            {
                if (!await context.EntityExists(entityType, key))
                {
                    context.Problems.Add(At($"No {entityType} with key {key}, live or in this change set."));
                }
            }
        }

        if (field.Type == FieldType.ItemReference && field.ReferencesItemIn is { } itemsAlias)
        {
            var key = Guid.Parse(value.GetValue<string>());
            if (EntityValues.FindItem(context.Entity.Fields[itemsAlias] as JsonArray, key) is null)
            {
                context.Problems.Add(At($"No item with key {key} in '{itemsAlias}'."));
            }
        }
    }

    /// <summary>
    /// The variants a value is checked in: once if it doesn't vary, else each variant the object exists in, narrowed to
    /// what it varies by. A dimension with listed values (cultures) must have one; an open one (segments) may be the default.
    /// </summary>
    private static IEnumerable<VariantId> Targets(EntitySchema schema, IReadOnlyList<string> variesBy, IReadOnlyList<VariantId> variants)
    {
        if (variesBy.Count == 0)
        {
            return [VariantId.Invariant];
        }

        return variants
            .Select(v => v.Narrow(variesBy))
            .Where(v => variesBy.All(d => schema.Dimension(d)?.Values is null || v[d] is not null))
            .Distinct();
    }
}

/// <summary>Writes an object schema as JSON Schema, for models and editors. Object fields nest; items fields list their item types.</summary>
public static class SchemaWriter
{
    /// <summary>
    /// The JSON Schema of an object schema's fields. An items field lists the item types it holds, offering only those
    /// <paramref name="userKey"/> may use, when given; an item type's own fields are fetched in a second step.
    /// </summary>
    public static JsonObject Fields(ObjectSchema schema, ItemTypes? itemTypes = null, Guid? userKey = null)
    {
        var properties = new JsonObject();
        foreach (var field in schema.Fields)
        {
            if (field.IsItems)
            {
                properties[field.Alias] = Items(field, itemTypes, userKey);
                continue;
            }

            var fieldJson = field.Type == FieldType.Object && field.Schema is { } objectSchema ? Fields(objectSchema, itemTypes, userKey) : Single(field);
            if (field.Many)
            {
                fieldJson = new JsonObject { ["type"] = "array", ["items"] = fieldJson };
            }

            fieldJson["description"] = field.Description;
            if (field.Handler is { } handler)
            {
                fieldJson["x-edit"] = handler.Usage;
            }

            if (field.AcceptsBindings)
            {
                fieldJson["x-bindings"] = true;
            }

            if (field.Default is { } preset)
            {
                fieldJson["default"] = preset.DeepClone();
            }

            if (field.Varies)
            {
                fieldJson["x-variations"] = new JsonArray(field.VariesBy.Select(d => (JsonNode?)d).ToArray());
            }

            properties[field.Alias] = fieldJson;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(schema.Fields.Where(f => f.Required && !f.IsItems).Select(f => (JsonNode?)f.Alias).ToArray()),
        };
    }

    private static JsonObject Items(FieldDefinition field, ItemTypes? itemTypes, Guid? userKey)
    {
        var schema = new JsonObject
        {
            ["type"] = "array",
            ["description"] = field.Description,
            ["x-edit"] = "Items: each has a key and a type. Add, remove and move them with entity.addItem, removeItem and moveItem at this field's path; set their fields at '<this path>/{key}/<field>'.",
            ["x-item-types"] = new JsonArray((itemTypes?.InGroup(field.ItemTypeGroup!) ?? [])
                .Where(t => userKey is null || itemTypes!.IsAvailable(t))
                .Select(t => (JsonNode?)new JsonObject { ["alias"] = t.Alias, ["group"] = t.Group, ["name"] = t.Name, ["description"] = t.Description })
                .ToArray()),
        };

        if (field.Min is { } min)
        {
            schema["minItems"] = min;
        }

        if (field.Max is { } max)
        {
            schema["maxItems"] = max;
        }

        if (field.Varies)
        {
            schema["x-variations"] = new JsonArray(field.VariesBy.Select(d => (JsonNode?)d).ToArray());
        }

        return schema;
    }

    private static JsonObject Single(FieldDefinition field)
    {
        if (field.Type == FieldType.Json)
        {
            return (JsonObject?)field.JsonSchema?.DeepClone() ?? new JsonObject();
        }

        if (field.Type == FieldType.EntityReference && field.ReferencesAnyOf is { Count: > 0 } types)
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["entityType"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(types.Select(t => (JsonNode?)t).ToArray()) },
                    ["key"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                },
                ["required"] = new JsonArray("entityType", "key"),
            };
        }

        var schema = new JsonObject
        {
            ["type"] = field.Type switch
            {
                FieldType.Number => "number",
                FieldType.Integer => "integer",
                FieldType.Boolean => "boolean",
                _ => "string",
            },
        };

        switch (field.Type)
        {
            case FieldType.Date:
                schema["format"] = "date";
                break;
            case FieldType.EntityReference:
                schema["format"] = "uuid";
                schema["x-references"] = field.ReferencesEntityType;
                break;
            case FieldType.ItemReference:
                schema["format"] = "uuid";
                schema["x-references-item-in"] = field.ReferencesItemIn;
                break;
        }

        if (field.Allowed is { Count: > 0 } allowed)
        {
            schema["enum"] = new JsonArray(allowed.Select(a => (JsonNode?)a).ToArray());
        }

        return schema;
    }
}
