namespace Umbraco.Abilities.Entities;

/// <summary>
/// What a package implements once per entity type. The framework derives the generic entity
/// abilities (schema, create, edit, find, validate) from it.
/// </summary>
public interface IEntityAdapter
{
    string EntityType { get; }

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

    Entity New(Guid key, string? subtype);

    Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default);

    /// <summary>Domain rules beyond the schema (ranges, dates in order, uniqueness). Problems by path.</summary>
    Task<IReadOnlyList<Problem>> ValidateAsync(Entity entity, CancellationToken cancellationToken = default);

    Task<EntitySummary> SaveAsync(Entity entity, CancellationToken cancellationToken = default);

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

public sealed record EntitySubtype(string Alias, string Name);

public sealed class EntityAdapterRegistry
{
    private readonly Dictionary<string, IEntityAdapter> _definitions = new(StringComparer.Ordinal);

    public EntityAdapterRegistry Add(IEntityAdapter adapter)
    {
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
