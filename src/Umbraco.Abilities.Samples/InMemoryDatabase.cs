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

    public Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) => Task.FromResult<IUnitOfWork>(new Transaction(this, Snapshot()));

    private Dictionary<string, Dictionary<Guid, Row>> Snapshot()
        => _tables.ToDictionary(t => t.Key, t => t.Value.ToDictionary(r => r.Key, r => r.Value with { Root = (JsonObject)r.Value.Root.DeepClone() }));

    private sealed class Transaction(InMemoryDatabase database, Dictionary<string, Dictionary<Guid, Row>> snapshot) : IUnitOfWork
    {
        private bool _completed;

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            _completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                database._tables = snapshot;
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Sample storage for a typed model: the model is kept in its entity form. A real product loads and saves its own
/// model instead (Commerce's discount service, for example), and its adapter is just as short.
/// </summary>
public abstract class InMemoryModelAdapter<TModel>(InMemoryDatabase database, ItemTypeRegistry itemTypes, string editUrlPrefix)
    : EntityAdapterBase<TModel>(itemTypes, database)
    where TModel : class
{
    protected InMemoryDatabase Database { get; } = database;

    public SampleAccess? Access { get; set; }

    public override Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
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

    protected override IEnumerable<TModel> ListModels() => All();

    protected override string? GetEditUrl(Guid key) => $"{editUrlPrefix}/{key}";

    /// <summary>A stored version, for products that keep version history.</summary>
    protected Entity? LoadVersion(Guid key, long version)
        => Database.GetVersion(EntityType, key, version) is { } row ? FromRow(key, row) : null;

    private Entity FromRow(Guid key, InMemoryDatabase.Row row)
        => new() { EntityType = EntityType, Key = key, Version = row.Version, Fields = row.Root };
}
