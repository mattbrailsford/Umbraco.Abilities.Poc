using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities.Entities;

/// <summary>
/// An entity edit that only some entity types support: those whose adapter implements an interface the edit names.
/// Core's delete and move are built this way, and another package ships its own the same way: it defines an adapter
/// interface (say <c>ILockableEntityAdapter</c>) and one ability on it. A product opts in by implementing the
/// interface; no product writes the ability itself. <c>entity.getSchema</c> lists each supported one under
/// capabilities.
/// </summary>
public interface IEntityCapability : IEntityEditAbility
{
    /// <summary>What <c>entity.getSchema</c> lists under capabilities, e.g. <c>"delete"</c>.</summary>
    string Capability { get; }

    /// <summary>Whether an entity type's adapter supports the edit.</summary>
    bool Supports(IEntityAdapter adapter);
}

public static class EntityCapabilities
{
    /// <summary>What an entity type can do beyond setting values: each registered capability its adapter supports.</summary>
    public static IReadOnlyList<string> Of(IEntityAdapter adapter, AbilityRegistry abilities)
        => abilities.All.OfType<IEntityCapability>()
            .Where(a => a.Supports(adapter))
            .Select(a => a.Capability)
            .Order(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// The base for an edit that needs an adapter interface. It refuses an entity type whose adapter doesn't implement
/// <typeparamref name="TAdapter"/>, with a problem the caller can act on, then hands over the adapter as that interface.
/// It is recorded, replayed, rebased and applied like any edit. A change it makes to values is checked as an update,
/// and a change to a tree's parent as a move.
/// </summary>
public abstract class EntityCapabilityBase<TAdapter, TInput> : EntityEditAbilityBase<TInput>, IEntityCapability
    where TAdapter : class
    where TInput : IEntityEditInput
{
    public abstract string Capability { get; }

    public bool Supports(IEntityAdapter adapter) => adapter is TAdapter;

    protected sealed override object? Apply(Entity entity, TInput input, EntityEditContext context)
    {
        if (context.Adapter is not TAdapter adapter)
        {
            throw new AbilityInputException(
                $"{context.Adapter.DisplayName} doesn't support {Capability}. entity.getSchema lists what an entity type supports under capabilities.",
                [new Problem("entityType", $"Doesn't support {Capability}.")]);
        }

        return Apply(entity, adapter, input, context);
    }

    /// <summary>Applies the edit to a working copy. Throw <see cref="AbilityInputException"/> for input the caller can fix.</summary>
    protected abstract object? Apply(Entity entity, TAdapter adapter, TInput input, EntityEditContext context);
}
