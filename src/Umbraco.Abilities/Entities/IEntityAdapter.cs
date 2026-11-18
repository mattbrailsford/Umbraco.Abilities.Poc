namespace Umbraco.Abilities.Entities;

/// <summary>
/// What a package implements once per entity type. The framework derives the generic entity
/// abilities (schema, create, edit, find, validate) from it.
/// </summary>
public interface IEntityAdapter
{
    string EntityType { get; }

    /// <summary>
    /// What a person calls this entity type, such as "Discount" or "Document", for questions and summaries they read.
    /// Optional: without it, the entity type is shown.
    /// </summary>
    string DisplayName => EntityType;

    /// <summary>False for entities that can be found and referenced but not created here.</summary>
    bool CanCreate { get; }

    /// <summary>The product transaction this entity type saves in. Entity types sharing one apply together.</summary>
    ChangeSets.IUnitOfWorkProvider UnitOfWork { get; }

    /// <summary>
    /// The subtypes an entity of this type can be created as, each with its own schema, such as the document
    /// types of CMS content. Empty when the entity type has a single schema.
    /// </summary>
    Task<IReadOnlyList<EntitySubtype>> GetSubtypesAsync(CancellationToken cancellationToken = default);

    /// <summary>The schema of a subtype (null when there are none). May depend on what is installed.</summary>
    Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default);

    /// <summary>
    /// A new entity, before any values are set: where a product fills in defaults. Asynchronous, since defaults can
    /// depend on the subtype, such as a document type's default values.
    /// </summary>
    Task<Entity> NewAsync(Guid key, string? subtype, CancellationToken cancellationToken = default);

    Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default);

    /// <summary>Domain rules beyond the schema (ranges, dates in order, uniqueness). Problems by path.</summary>
    Task<IReadOnlyList<Problem>> ValidateAsync(Entity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an entity. <paramref name="before"/> is the entity as it is live now, loaded just before the save (null when
    /// creating), so the adapter can see what changed and do what the product needs for it, such as running a move
    /// when the parent changed.
    /// </summary>
    Task<EntitySummary> SaveAsync(Entity? before, Entity entity, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default);

    EntitySummary ToSummary(Entity entity);
}

/// <summary>
/// An adapter whose product keeps version history. A change set then only remembers which version it started
/// editing from, and loads that version from the history when it needs to see what changed since, instead of
/// keeping its own snapshot.
/// </summary>
public interface IVersionedEntityAdapter : IEntityAdapter
{
    /// <summary>A stored version, as <see cref="IEntityAdapter.LoadAsync"/> returned it then; null if the history no longer has it.</summary>
    Task<Entity?> LoadVersionAsync(Guid key, long version, CancellationToken cancellationToken = default);
}

/// <summary>
/// An adapter whose entities can be deleted. The framework then offers <c>entity.delete</c> for its entity type: the
/// delete is recorded in a change set like any edit, the entity reads as gone inside the set, and it is deleted when the
/// set is applied, after the set's other changes to the product are saved.
/// </summary>
public interface IDeletableEntityAdapter : IEntityAdapter
{
    /// <summary>True when the product can restore a deleted entity, such as from the CMS recycle bin.</summary>
    bool CanRestore { get; }

    /// <summary>Deletes the entity, in the product's unit of work. Not called for adapters that keep their own drafts: their draft applies the delete.</summary>
    Task DeleteAsync(Guid key, CancellationToken cancellationToken = default);
}

/// <summary>
/// An adapter whose entities sit in a tree. The framework then offers <c>entity.move</c> for its entity type, which sets
/// <see cref="ParentField"/>. The adapter saves a changed parent as a move, in <see cref="IEntityAdapter.SaveAsync"/>
/// (or its draft). A change to the parent field, by any ability, is checked as <see cref="EntityPermission.Move"/>.
/// </summary>
public interface ITreeEntityAdapter : IEntityAdapter
{
    /// <summary>The alias of a root reference field holding the parent's key; no value means the entity is at the root.</summary>
    string ParentField { get; }
}

public sealed record EntitySubtype(string Alias, string Name);

public sealed class EntityAdapterRegistry
{
    private readonly Dictionary<string, IEntityAdapter> _definitions = new(StringComparer.Ordinal);

    /// <summary>Adds an adapter. It must check access (<see cref="IAuthorizingEntityAdapter"/>): one that doesn't is refused.</summary>
    public EntityAdapterRegistry Add(IEntityAdapter adapter)
    {
        if (adapter is not IAuthorizingEntityAdapter)
        {
            throw new InvalidOperationException(
                $"The {adapter.EntityType} adapter doesn't check access. Implement IAuthorizingEntityAdapter and ask the product's permission model; return null from AuthorizeAsync only if everyone may read and change these entities.");
        }

        _definitions.Add(adapter.EntityType, adapter);
        EntityUdis.Register(adapter.EntityType);
        return this;
    }

    public IEntityAdapter? Find(string entityType) => _definitions.GetValueOrDefault(entityType);

    public IEntityAdapter Get(string entityType)
        => Find(entityType) ?? throw new AbilityInputException(
            $"Unknown entity type '{entityType}'.",
            [new Problem("entityType", $"Known types: {string.Join(", ", _definitions.Keys)}.")]);

    public IReadOnlyCollection<IEntityAdapter> All => _definitions.Values;
}
