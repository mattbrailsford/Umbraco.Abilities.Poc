using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities.Entities.Models;

/// <summary>
/// The base for an entity adapter whose entities are C# classes. A package implements loading and saving its own
/// model; the framework works out the schema, maps the model to and from entities, and runs the model's
/// DataAnnotations. Everything else (schemas, generic edit abilities, change sets, rebasing) comes for free.
/// </summary>
/// <typeparam name="TModel">The model class, marked with <see cref="EntityAttribute"/> and a <c>[Key]</c> Guid property.</typeparam>
public abstract class EntityAdapterBase<TModel> : IAuthorizingEntityAdapter
    where TModel : class
{
    private readonly (string EntityType, string Name, string Description) _entity;
    private readonly ModelInfo _info;

    protected EntityAdapterBase(ItemTypeRegistry itemTypes, IUnitOfWorkProvider unitOfWork)
    {
        ItemTypes = itemTypes;
        UnitOfWork = unitOfWork;
        Configure(new ModelConfigurator<TModel>());
        _info = ModelSchemas.Info(typeof(TModel));
        _entity = _info.Entity
            ?? throw new InvalidOperationException($"{typeof(TModel).Name} is not an entity: add [Entity] or configure it with IsEntity.");
        if (_info.Key is null || _info.Key.PropertyType != typeof(Guid))
        {
            throw new InvalidOperationException($"{typeof(TModel).Name} needs a Guid key: add [Key] or configure HasKey.");
        }

        itemTypes.AddModel(typeof(TModel));
        foreach (var source in ItemTypeSources())
        {
            itemTypes.AddSource(source);
        }
    }

    public string EntityType => _entity.EntityType;

    public virtual bool CanCreate => true;

    public IUnitOfWorkProvider UnitOfWork { get; }

    protected internal ItemTypeRegistry ItemTypes { get; }

    // ── What a package implements ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Describes, fluently, model classes the package can't put attributes on, such as Management API request models.
    /// Called once, before the model is first described. Optional.
    /// </summary>
    protected virtual void Configure(ModelConfigurator<TModel> models)
    {
    }

    /// <summary>
    /// The plugin seams the model's items fields name by group: one source per group, such as a
    /// <see cref="PluginItemTypeSource"/> over the product's provider collection. Called once, when the adapter is
    /// created. Item types that are classes need nothing here. Optional.
    /// </summary>
    protected virtual IEnumerable<IItemTypeSource> ItemTypeSources() => [];

    /// <summary>Loads the model, or null if there is none with this key. Set its [EntityVersion] if it has one.</summary>
    public abstract Task<TModel?> LoadModelAsync(Guid key, CancellationToken cancellationToken = default);

    /// <summary>Saves the model, creating it when it is new. Throw to fail the save; the product's transaction rolls back.</summary>
    public abstract Task SaveModelAsync(TModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user may read, create or change the entity, asked of the product's own permission model. Null when
    /// allowed; otherwise why not, in words the user can be told. Required: an adapter can't leave access open by
    /// forgetting it, as an ability can't.
    /// </summary>
    public abstract Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finds models by name. Optional.</summary>
    protected virtual Task<IReadOnlyList<TModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TModel>>([]);

    /// <summary>Domain rules the attributes can't express, e.g. dates in order or unique codes. Optional.</summary>
    protected virtual Task<IReadOnlyList<Problem>> ValidateModelAsync(TModel model, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Problem>>([]);

    /// <summary>The values a dimension the model varies by can have, e.g. the site's languages for culture. Null: any value.</summary>
    protected virtual Task<IReadOnlyList<string>?> GetDimensionValuesAsync(string dimension, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>?>(null);

    protected virtual string? GetEditUrl(Guid key) => null;

    // ── Helpers for problems ───────────────────────────────────────────────────────────────────────

    /// <summary>A problem on a property of the model, e.g. <c>ProblemAt(d => d.EndDate, "...")</c>.</summary>
    protected static Problem ProblemAt<TValue>(Expression<Func<TModel, TValue>> property, string message)
        => new(AliasOf(property), message);

    /// <summary>A problem on a property of an item in the model, e.g. <c>ProblemAt(reward, r => r.Percent, "...")</c>.</summary>
    protected static Problem ProblemAt<TItem, TValue>(TItem item, Expression<Func<TItem, TValue>> property, string message)
        where TItem : class
        => new(EntityEditor.Join(ModelSchemas.PathOf(item), AliasOf(property)), message);

    // ── IEntityAdapter, done by the framework ───────────────────────────────────────────────────

    public Task<IReadOnlyList<EntitySubtype>> GetSubtypesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntitySubtype>>([]);

    public async Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default)
    {
        var dimensions = new List<VariationDimension>();
        foreach (var name in _info.Dimensions)
        {
            dimensions.Add(new VariationDimension(name, $"Values vary by {name}.", await GetDimensionValuesAsync(name, cancellationToken)));
        }

        return new EntitySchema(EntityType, _entity.Name, _entity.Description, _info.Schema) { Dimensions = dimensions };
    }

    public Entity New(Guid key, string? subtype) => new() { EntityType = EntityType, Key = key };

    public async Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default)
        => await LoadModelAsync(key, cancellationToken) is { } model ? ToEntity(model) : null;

    public async Task<IReadOnlyList<Problem>> ValidateAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var model = ToModel(entity);
        return [.. ModelSchemas.Validate(model), .. await ValidateModelAsync(model, cancellationToken)];
    }

    public async Task<EntitySummary> SaveAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        await SaveModelAsync(ToModel(entity), cancellationToken);
        return ToSummary(entity);
    }

    public async Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default)
        => (await FindModelsAsync(query, cancellationToken)).Select(m => ToSummary(ToEntity(m))).ToList();

    public EntitySummary ToSummary(Entity entity)
    {
        var name = _info.Name is { } nameProperty ? EntityValues.ReadAny(entity.Fields, ModelSchemas.AliasOf(nameProperty))?.GetValue<string>() : null;
        return new EntitySummary(EntityType, entity.Key, name ?? "(unnamed)", GetEditUrl(entity.Key));
    }

    /// <summary>The model an entity describes.</summary>
    public TModel ToModel(Entity entity)
    {
        var model = (TModel)ModelSchemas.Read(typeof(TModel), entity.Fields, ItemTypes, string.Empty);
        _info.Key!.SetValue(model, entity.Key);
        _info.Version?.SetValue(model, entity.Version);
        return model;
    }

    /// <summary>The entity for a model.</summary>
    public Entity ToEntity(TModel model)
    {
        var entity = new Entity
        {
            EntityType = EntityType,
            Key = (Guid)_info.Key!.GetValue(model)!,
            Version = _info.Version?.GetValue(model) is { } version ? Convert.ToInt64(version) : null,
        };
        entity.Fields = ModelSchemas.Write(model);
        return entity;
    }

    private static string AliasOf(LambdaExpression property) => ModelSchemas.AliasOf(ModelBuilder<TModel>.PropertyOf(property));
}

/// <summary>
/// A product ability that changes one entity, written against its model: <see cref="Edit"/> changes the model. Run
/// directly, it loads, edits and saves the entity; inside a change set it is recorded as a change to the entity, shown
/// in its working state and saved with the entity's product.
/// </summary>
public abstract class ModelEditAbilityBase<TInput, TModel>(EntityAdapterBase<TModel> adapter)
    : AbilityBase<TInput, EntitySummary>, IEntityEditAbility
    where TModel : class
{
    /// <summary>The key of the entity the input edits.</summary>
    protected abstract Guid KeyOf(TInput input);

    /// <summary>Changes the model. Throw <see cref="AbilityInputException"/> for a correctable problem.</summary>
    protected abstract void Edit(TModel model, TInput input);

    public EntityEditTarget Prepare(JsonObject input)
        => new(adapter.EntityType, KeyOf(Read(input)), Creates: false);

    public object? Apply(Entity entity, JsonObject input, EntityEditContext context)
    {
        var model = adapter.ToModel(entity);
        Edit(model, Read(input));
        entity.Fields = adapter.ToEntity(model).Fields;
        return adapter.ToSummary(entity);
    }

    protected override async Task<AbilityResult> ExecuteAsync(TInput input, AbilityContext context, CancellationToken cancellationToken)
    {
        var key = KeyOf(input);
        var model = await adapter.LoadModelAsync(key, cancellationToken)
            ?? throw new AbilityInputException($"No {adapter.EntityType} with key {key}.", [new Problem("key", "Not found.")]);
        var before = adapter.ToEntity(model);
        Edit(model, input);

        // Run directly, the ability saves: the user must be allowed to change the entity, as inside a change set.
        var after = adapter.ToEntity(model);
        if (await EntityAccess.CheckAsync(adapter, new EntityAccessRequest(context.UserKey, EntityPermission.Update, key, after, EntityAccess.VariantsChanged(before, after, ModelSchemas.SchemaOf(typeof(TModel)), adapter.ItemTypes)), cancellationToken) is { } denied)
        {
            return EntityAccess.Denied(denied);
        }

        await adapter.SaveModelAsync(model, cancellationToken);
        return Success(adapter.ToSummary(adapter.ToEntity(model)));
    }

    private static TInput Read(JsonObject input)
        => System.Text.Json.JsonSerializer.Deserialize<TInput>(input, AbilityJson.Options) ?? throw new AbilityInputException("Input is required.");
}

/// <summary>
/// Draft-aware reads for a product's own Read abilities, in its model's terms. Inside a change set they show the set's
/// changes; outside one, live data. A product runs its own query for what's live, then reads it through these.
/// </summary>
public static class ChangeSetModelReads
{
    /// <summary>One model as the current call sees it; null if it doesn't exist, or the current set deleted it.</summary>
    public static async Task<TModel?> ReadModelAsync<TModel>(this ChangeSetService changeSets, EntityAdapterBase<TModel> adapter, Guid key, CancellationToken cancellationToken = default)
        where TModel : class
        => await changeSets.ReadAsync(adapter, key, cancellationToken) is { } entity ? adapter.ToModel(entity) : null;

    /// <summary>
    /// The models a product's query found live, as the current call sees them, plus any the current set created.
    /// Filter after reading.
    /// </summary>
    public static async Task<IReadOnlyList<TModel>> ReadModelsAsync<TModel>(this ChangeSetService changeSets, EntityAdapterBase<TModel> adapter, IEnumerable<Guid> liveKeys, CancellationToken cancellationToken = default)
        where TModel : class
        => (await changeSets.ReadAllAsync(adapter, liveKeys, cancellationToken)).Select(adapter.ToModel).ToList();
}
