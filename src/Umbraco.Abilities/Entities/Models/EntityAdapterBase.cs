using System.Collections.Concurrent;
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

        AllowItems(new ItemAllowList<TModel>(itemTypes));
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

    /// <summary>
    /// Every model, for finding by name. Optional: return them here and finding works, or override
    /// <see cref="FindModelsAsync"/> when the product can search itself.
    /// </summary>
    protected virtual IEnumerable<TModel> ListModels() => [];

    /// <summary>Finds models by name. By default, the models from <see cref="ListModels"/> whose name contains the query.</summary>
    protected virtual Task<IReadOnlyList<TModel>> FindModelsAsync(string query, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TModel>>(ListModels()
            .Where(m => ToSummary(ToEntity(m)).Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList());

    /// <summary>
    /// Domain rules the attributes can't express, e.g. dates in order or unique codes. Optional. Add each problem on
    /// the property it's about: <c>problems.At(d => d.EndDate, "Must be on or after startDate.")</c>.
    /// </summary>
    protected virtual void Validate(TModel model, ModelProblems<TModel> problems)
    {
    }

    /// <summary>Domain rules that need to wait for something, such as a remote check. Optional; most adapters use <see cref="Validate"/>.</summary>
    protected virtual Task ValidateModelAsync(TModel model, ModelProblems<TModel> problems, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>The values a dimension the model varies by can have, e.g. the site's languages for culture. Null: any value.</summary>
    protected virtual Task<IReadOnlyList<string>?> GetDimensionValuesAsync(string dimension, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>?>(null);

    protected virtual string? GetEditUrl(Guid key) => null;

    /// <summary>
    /// Item types allowed by a setting only known at run time, such as a Block List data type's allowed blocks:
    /// <c>allow.Field(x =&gt; x.Blocks, () =&gt; settings.AllowedBlocks)</c>. The list is read
    /// each time it's needed, so a changed setting applies straight away. Optional: without it, an items field allows
    /// the classes its attribute names, or every class that extends its element type.
    /// </summary>
    protected virtual void AllowItems(ItemAllowList<TModel> allow)
    {
    }

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
        var problems = new ModelProblems<TModel>();
        Validate(model, problems);
        await ValidateModelAsync(model, problems, cancellationToken);
        return [.. ModelSchemas.Validate(model), .. problems];
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
}

/// <summary>Limits items fields to the item types a run-time setting allows. See <c>AllowItems</c> on the adapter.</summary>
public sealed class ItemAllowList<TModel>(ItemTypeRegistry itemTypes)
    where TModel : class
{
    /// <summary>Allows only these item classes in a field of the model.</summary>
    public ItemAllowList<TModel> Field<TValue>(Expression<Func<TModel, TValue>> field, Func<IEnumerable<Type>> itemClasses)
        => Field<TModel, TValue>(field, itemClasses);

    /// <summary>Allows only the item types with these aliases in a field of the model, e.g. a plugin seam's <c>DynamicItem</c> field.</summary>
    public ItemAllowList<TModel> Field<TValue>(Expression<Func<TModel, TValue>> field, Func<IEnumerable<string>> aliases)
        => Field<TModel, TValue>(field, aliases);

    /// <summary>Allows only these item classes in a field of an item class inside the model.</summary>
    public ItemAllowList<TModel> Field<TOwner, TValue>(Expression<Func<TOwner, TValue>> field, Func<IEnumerable<Type>> itemClasses)
        where TOwner : class
        => Field(field, () => itemClasses().Select(c => ModelSchemas.Info(c).ItemType?.Alias
            ?? throw new InvalidOperationException($"{c.Name} is not an item type: add [ItemType] or configure it with IsItemType.")));

    /// <summary>Allows only the item types with these aliases in a field of an item class inside the model.</summary>
    public ItemAllowList<TModel> Field<TOwner, TValue>(Expression<Func<TOwner, TValue>> field, Func<IEnumerable<string>> aliases)
        where TOwner : class
    {
        var alias = ModelSchemas.AliasOf(ModelBuilder<TOwner>.PropertyOf(field));
        var group = ModelSchemas.SchemaOf(typeof(TOwner)).Find(alias)?.ItemTypeGroup
            ?? throw new InvalidOperationException($"{typeof(TOwner).Name}.{alias} is not an items field.");
        itemTypes.Allow(group, aliases);
        return this;
    }
}

/// <summary>
/// The problems an adapter's domain rules find, each on the path of the property it's about, so a model or a form can
/// put it next to the value to fix.
/// </summary>
public sealed class ModelProblems<TModel> : IEnumerable<Problem>
    where TModel : class
{
    private readonly List<Problem> _problems = [];

    /// <summary>A problem on a property of the model, e.g. <c>problems.At(d => d.EndDate, "...")</c>.</summary>
    public void At<TValue>(Expression<Func<TModel, TValue>> property, string message)
        => _problems.Add(new Problem(AliasOf(property), message));

    /// <summary>A problem on a property of an item in the model, e.g. <c>problems.At(reward, r => r.Percent, "...")</c>.</summary>
    public void At<TItem, TValue>(TItem item, Expression<Func<TItem, TValue>> property, string message)
        where TItem : class
        => _problems.Add(new Problem(EntityEditor.Join(ModelSchemas.PathOf(item), AliasOf(property)), message));

    /// <summary>A problem with its own path.</summary>
    public void Add(Problem problem) => _problems.Add(problem);

    public IEnumerator<Problem> GetEnumerator() => _problems.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

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
    private static readonly ConcurrentDictionary<string, PropertyInfo?> KeyProperties = new();

    /// <summary>
    /// The key of the entity the input edits. By default, the input's Guid property marked
    /// <c>[EntityKey]</c> for the adapter's entity type. Override it when the key comes from somewhere else.
    /// </summary>
    protected virtual Guid KeyOf(TInput input)
    {
        var property = KeyProperties.GetOrAdd(adapter.EntityType, entityType => typeof(TInput).GetProperties()
            .SingleOrDefault(p => p.PropertyType == typeof(Guid) && p.GetCustomAttribute<EntityKeyAttribute>()?.EntityTypes.Contains(entityType) == true))
            ?? throw new InvalidOperationException(
                $"{typeof(TInput).Name} has no Guid property marked [EntityKey(\"{adapter.EntityType}\")]. Mark the one that names the entity to edit, or override KeyOf.");
        return (Guid)property.GetValue(input)!;
    }

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
