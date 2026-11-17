using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities.Entities;

/// <summary>
/// An edit another package ships, which works on every entity type that has the fields it needs. The two meet on
/// field roles (<see cref="FieldDefinition.Role"/>): the behaviour names the roles it needs, and an adapter tags its
/// fields with them. Neither references the other, so installing the behaviour's package is enough for it to work on
/// every entity type that has those roles.
/// <para>
/// A behaviour is an ability like any other: registered in the <see cref="AbilityRegistry"/>, recorded in a change set,
/// replayed, rebased and applied with the entity's other edits. It only changes values, so the adapter saves it as it
/// saves any field. A behaviour that needs the product to do more than save a value isn't covered.
/// </para>
/// </summary>
public interface IEntityBehaviour : IEntityEditAbility
{
    /// <summary>What <c>entity.getSchema</c> lists under capabilities for the entity types that support it, e.g. <c>"archive"</c>.</summary>
    string Name { get; }

    /// <summary>The roles an entity type needs a root field for, each, to support the behaviour.</summary>
    IReadOnlyList<string> Roles { get; }
}

public static class EntityBehaviours
{
    /// <summary>Whether an entity type's schema has a root field in every role the behaviour needs.</summary>
    public static bool Supports(this IEntityBehaviour behaviour, ObjectSchema schema)
        => behaviour.Roles.All(role => schema.Fields.Any(f => f.Role == role));

    /// <summary>
    /// What an entity type can do beyond editing values: delete and move when its adapter supports them, then each
    /// registered behaviour its fields' roles support. Listed by <c>entity.getSchema</c>.
    /// </summary>
    public static IReadOnlyList<string> Capabilities(IEntityAdapter adapter, ObjectSchema schema, AbilityRegistry abilities)
    {
        var capabilities = new List<string>();
        if (adapter is IDeletableEntityAdapter)
        {
            capabilities.Add("delete");
        }

        if (adapter is ITreeEntityAdapter)
        {
            capabilities.Add("move");
        }

        capabilities.AddRange(abilities.All.OfType<IEntityBehaviour>().Where(b => b.Supports(schema)).Select(b => b.Name).Order(StringComparer.Ordinal));
        return capabilities;
    }
}

/// <summary>
/// The base for a behaviour. It refuses an entity type without the roles it needs, with a problem the caller can act on,
/// then hands the entity to <see cref="Apply(BehaviourTarget, TInput)"/>, which reads and sets values by role.
/// </summary>
public abstract class EntityBehaviourBase<TInput> : EntityEditAbilityBase<TInput>, IEntityBehaviour
    where TInput : IEntityEditInput
{
    public abstract string Name { get; }

    public abstract IReadOnlyList<string> Roles { get; }

    protected sealed override object? Apply(Entity entity, TInput input, EntityEditContext context)
    {
        if (!this.Supports(context.Schema.Root))
        {
            throw new AbilityInputException(
                $"{context.Adapter.DisplayName} doesn't support {Name}. entity.getSchema lists what an entity type supports under capabilities.",
                [new Problem("entityType", $"Doesn't support {Name}.")]);
        }

        return Apply(new BehaviourTarget(Target(entity, input, context)), input);
    }

    /// <summary>Applies the behaviour to a working copy. Throw <see cref="AbilityInputException"/> for input the caller can fix.</summary>
    protected abstract object? Apply(BehaviourTarget target, TInput input);
}

/// <summary>The entity a behaviour edits, with its values reached by role rather than by the adapter's field names.</summary>
public sealed class BehaviourTarget(EditTarget target)
{
    public Entity Entity => target.Entity;

    /// <summary>The root field with a role.</summary>
    public FieldDefinition Field(string role)
        => target.Schema.Root.Fields.FirstOrDefault(f => f.Role == role)
            ?? throw new InvalidOperationException($"No field has the role '{role}'.");

    /// <summary>The value of the field with a role, in the variant the call names.</summary>
    public JsonNode? Get(string role) => EntityValues.Get(target.Entity.Fields, Field(role), target.Variant);

    /// <summary>Sets the value of the field with a role, checked against the field's schema like any value. Returns its path.</summary>
    public string Set(string role, JsonNode? value) => EntityEditor.SetValue(target, Field(role).Alias, value);
}
