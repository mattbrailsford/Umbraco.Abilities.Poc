using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples;

/// <summary>
/// Stands in for one product's database and transaction. Each sample product has its own, as Commerce, Automate
/// and Umbraco.AI can each use their own connection string.
/// </summary>
public sealed class InMemoryDatabase(string name) : IUnitOfWorkProvider
{
    public string Name { get; } = name;

    private Dictionary<string, Dictionary<Guid, Row>> _tables = [];

    // Every saved version, for products that keep version history.
    private readonly Dictionary<(string EntityType, Guid Key, long Version), JsonObject> _history = [];

    public sealed record Row(JsonObject Root, long Version);

    public Row? Get(string entityType, Guid key)
        => _tables.TryGetValue(entityType, out var table) && table.TryGetValue(key, out var row)
            ? row with { Root = (JsonObject)row.Root.DeepClone() }
            : null;

    public IEnumerable<(Guid Key, Row Row)> All(string entityType)
        => _tables.TryGetValue(entityType, out var table) ? table.Select(kv => (kv.Key, kv.Value)).ToList() : [];

    public long Put(string entityType, Guid key, JsonObject root)
    {
        if (!_tables.TryGetValue(entityType, out var table))
        {
            table = [];
            _tables[entityType] = table;
        }

        var version = table.TryGetValue(key, out var existing) ? existing.Version + 1 : 1;
        table[key] = new Row((JsonObject)root.DeepClone(), version);
        _history[(entityType, key, version)] = (JsonObject)root.DeepClone();
        return version;
    }

    public Row? GetVersion(string entityType, Guid key, long version)
        => _history.TryGetValue((entityType, key, version), out var root) ? new Row((JsonObject)root.DeepClone(), version) : null;

    public void ForgetHistory()
    {
        _history.Clear();
    }

    public IUnitOfWork Begin() => new Transaction(this, Snapshot());

    private Dictionary<string, Dictionary<Guid, Row>> Snapshot()
        => _tables.ToDictionary(t => t.Key, t => t.Value.ToDictionary(r => r.Key, r => r.Value with { Root = (JsonObject)r.Value.Root.DeepClone() }));

    private sealed class Transaction(InMemoryDatabase database, Dictionary<string, Dictionary<Guid, Row>> snapshot) : IUnitOfWork
    {
        private bool _completed;

        public void Complete() => _completed = true;

        public void Dispose()
        {
            if (!_completed)
            {
                database._tables = snapshot;
            }
        }
    }
}

/// <summary>
/// Sample storage for a typed model: the model is kept in its entity form. A real product loads and saves its own
/// model instead (Commerce's discount service, for example), and its adapter is just as short.
/// </summary>
public abstract class InMemoryModelAdapter<TModel>(InMemoryDatabase database, ItemTypeRegistry itemTypes, string editUrlPrefix)
    : EntityAdapterBase<TModel>(itemTypes, database), IAuthorizingEntityAdapter
    where TModel : class
{
    protected InMemoryDatabase Database { get; } = database;

    public SampleAccess? Access { get; set; }

    public Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(Access?.Authorize(EntityType, request));

    public override Task<TModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default)
        => Task.FromResult(Database.Get(EntityType, key) is { } row ? ToModel(FromRow(key, row)) : null);

    public override Task SaveModelAsync(TModel model, CancellationToken cancellationToken = default)
    {
        var entity = ToEntity(model);
        Database.Put(EntityType, entity.Key, entity.Fields);
        return Task.CompletedTask;
    }

    /// <summary>Every stored model, for checks across entities such as unique codes.</summary>
    protected IEnumerable<TModel> All() => Database.All(EntityType).Select(r => ToModel(FromRow(r.Key, r.Row)));

    protected override Task<IReadOnlyList<TModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TModel>>(All().Where(m => ToSummary(ToEntity(m)).Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

    protected override string? GetEditUrl(Guid key) => $"{editUrlPrefix}/{key}";

    /// <summary>A stored version, for products that keep version history.</summary>
    protected Entity? LoadVersion(Guid key, long version)
        => Database.GetVersion(EntityType, key, version) is { } row ? FromRow(key, row) : null;

    private Entity FromRow(Guid key, InMemoryDatabase.Row row)
        => new() { EntityType = EntityType, Key = key, Version = row.Version, Fields = row.Root };
}

/// <summary>
/// An adapter written against entities rather than a model class: the escape hatch for entities whose
/// schema isn't a C# class, such as an Automate workflow whose step types are other packages' abilities.
/// </summary>
public abstract class InMemoryEntityAdapter(InMemoryDatabase database, string entityType, string nameField, string editUrlPrefix)
    : IAuthorizingEntityAdapter
{
    protected InMemoryDatabase Database { get; } = database;

    public SampleAccess? Access { get; set; }

    public Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(Access?.Authorize(EntityType, request));

    public string EntityType { get; } = entityType;

    public virtual bool CanCreate => true;

    public IUnitOfWorkProvider UnitOfWork => Database;

    public abstract Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<EntitySubtype>> GetSubtypesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySubtype>>([]);

    public Entity New(Guid key, string? subtype) => new() { EntityType = EntityType, Key = key };

    public Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default)
        => Task.FromResult(Database.Get(EntityType, key) is { } row
            ? new Entity { EntityType = EntityType, Key = key, Version = row.Version, Fields = row.Root }
            : null);

    public virtual Task<IReadOnlyList<Problem>> ValidateAsync(Entity entity, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Problem>>([]);

    public virtual Task<EntitySummary> SaveAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        Database.Put(EntityType, entity.Key, entity.Fields);
        return Task.FromResult(ToSummary(entity));
    }

    public Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySummary>>(Database.All(EntityType)
            .Select(r => ToSummary(new Entity { EntityType = EntityType, Key = r.Key, Fields = r.Row.Root }))
            .Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList());

    public EntitySummary ToSummary(Entity entity)
        => new(EntityType, entity.Key, EntityValues.ReadAny(entity.Fields, nameField)?.GetValue<string>() ?? "(unnamed)", $"{editUrlPrefix}/{entity.Key}");
}
