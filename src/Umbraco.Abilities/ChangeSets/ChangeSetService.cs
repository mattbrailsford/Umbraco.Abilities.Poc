using System.Text.Json;
using System.Text.Json.Nodes;

using Polly;
using Polly.Retry;

using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.HostedServices;

namespace Umbraco.Abilities.ChangeSets;

/// <summary>
/// Records Write ability calls instead of running them, works out an entity's state inside a set, and applies
/// the set. Unlike the CMS Draft Sets PoC, the working state is computed in memory from
/// <see cref="IEntityAdapter.LoadAsync"/> plus the recorded edits, so it works for any entity type.
/// <para>
/// Applying is atomic per product, not across products: each product's changes save in that product's own
/// transaction, one product after another, and side effects (such as sending an email) run last.
/// If a step fails, the steps before it stay applied, the set is kept, and applying again resumes from the
/// failed step.
/// </para>
/// </summary>
public sealed class ChangeSetService(
    IChangeSetStore store,
    AbilityRegistry abilities,
    EntityAdapterRegistry adapters,
    ItemTypeRegistry registry,
    IChangeSetAccessor accessor,
    IEventAggregator? events = null,
    IBackgroundTaskQueue? background = null,
    TimeSpan? retryDelay = null)
{
    public ChangeSet Create(string name)
    {
        var set = new ChangeSet { Name = name };
        store.Save(set);
        return set;
    }

    public ChangeSet Get(Guid id)
        => store.Get(id) ?? throw new AbilityInputException($"No change set {id}.", [new Problem("changeSetId", "Not found.")]);

    /// <summary>The change set, or null if there is none with that id (never created, applied or discarded).</summary>
    public ChangeSet? Find(Guid id) => store.Get(id);

    /// <summary>The change set the current call works in, from the ambient <see cref="IChangeSetAccessor"/>; null for none.</summary>
    public Guid? CurrentId => accessor.CurrentChangeSetId;

    /// <summary>The current change set, for abilities that need one. A correctable failure when none is open.</summary>
    public ChangeSet GetCurrent()
        => CurrentId is { } id ? Get(id) : throw new AbilityInputException(
            "No change set is open. Create one with changeSet_create, or name one with changeSetId.",
            [new Problem("changeSetId", "No change set is open.")]);

    /// <summary>Makes a change set (or none) current for the calls made until the scope is disposed.</summary>
    public IDisposable Use(Guid? changeSetId) => accessor.Use(changeSetId);

    /// <summary>
    /// Reads one entity the way the current call sees it: inside a change set, live data with the set's changes over
    /// it; otherwise live. A product's Read abilities use this, so they show what the set has built.
    /// </summary>
    public async Task<Entity?> ReadAsync(IEntityAdapter adapter, Guid key, CancellationToken cancellationToken = default)
        => CurrentId is { } id
            ? await GetWorkingStateAsync(Get(id), adapter.EntityType, key, cancellationToken)
            : await adapter.LoadAsync(key, cancellationToken);

    /// <summary>
    /// Reads the entities a product's own query found live, the way the current call sees them. Inside a change set,
    /// entities the set changed come with its changes, ones it created are added, and ones it deleted are left out.
    /// Filter after reading: what the set created or changed may not match the product's query.
    /// </summary>
    public async Task<IReadOnlyList<Entity>> ReadAllAsync(IEntityAdapter adapter, IEnumerable<Guid> liveKeys, CancellationToken cancellationToken = default)
    {
        var keys = liveKeys.ToList();
        if (CurrentId is { } id)
        {
            keys.AddRange(Get(id).Changes.Where(c => c.EntityType == adapter.EntityType).Select(c => c.EntityKey!.Value));
        }

        var entities = new List<Entity>();
        foreach (var key in keys.Distinct())
        {
            if (await ReadAsync(adapter, key, cancellationToken) is { } entity)
            {
                entities.Add(entity);
            }
        }

        return entities;
    }

    public async Task DiscardAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var set = Get(id);
        foreach (var (step, draftId) in set.Drafts.Where(d => !set.AppliedSteps.Contains(d.Key)))
        {
            await DraftingFor(step).DiscardDraftAsync(draftId, cancellationToken);
        }

        store.Remove(set.Id);
        if (events is not null)
        {
            await events.PublishAsync(new ChangeSetDiscardedNotification(set), cancellationToken);
        }
    }

    public async Task<AbilityResult> RecordAsync(Guid setId, IAbility ability, JsonObject input, AbilityContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var set = Get(setId);
            var recorded = (JsonObject)input.DeepClone();

            if (ability is not IEntityEditAbility edit)
            {
                // A side effect (send an email, call another system): not a change, so it runs after the set is applied.
                set.AfterApply.Add(new RecordedCall(ability.Descriptor.Id, recorded));
                store.Save(set);
                return AbilityResult.Ok(new
                {
                    recorded = true,
                    runsAfterApply = true,
                    note = "This is a side effect, not a change: it runs once every change in the set is applied, and can't be undone.",
                });
            }

            var target = edit.Prepare(recorded);
            var adapter = adapters.Get(target.EntityType);
            if (set.AppliedSteps.Contains(adapter.UnitOfWork.Name))
            {
                throw new AbilityInputException(
                    $"{adapter.UnitOfWork.Name} changes in this set are already applied. Start a new change set to change them again.",
                    [new Problem("entityType", "Already applied.")]);
            }

            var working = await GetWorkingStateAsync(set, target.EntityType, target.EntityKey, cancellationToken);
            var before = working?.Clone();

            if (target.Creates)
            {
                if (!adapter.CanCreate)
                {
                    throw new AbilityInputException($"{target.EntityType} cannot be created here.", [new Problem("entityType", "Not creatable.")]);
                }

                if (working is not null)
                {
                    throw new AbilityInputException($"{target.EntityType} {target.EntityKey} already exists.", [new Problem("key", "Already exists.")]);
                }

                var subtypes = await adapter.GetSubtypesAsync(cancellationToken);
                if (subtypes.Count > 0 && !subtypes.Any(s => s.Alias == target.Subtype))
                {
                    throw new AbilityInputException(
                        $"{target.EntityType} needs a subtype. Subtypes: {string.Join(", ", subtypes.Select(s => s.Alias))}.",
                        [new Problem("subtype", "Required, and must be one of the listed subtypes.")]);
                }

                working = await adapter.NewAsync(target.EntityKey, target.Subtype, cancellationToken);
            }
            else if (working is null)
            {
                throw new AbilityInputException($"No {target.EntityType} with key {target.EntityKey}.", [new Problem("key", "Not found.")]);
            }

            // Remember the version the set starts editing from, to rebase onto newer live versions later.
            // (Only for entities that exist live: one the set creates has nothing to rebase onto.)
            // (Products with their own drafts keep their own base.)
            if (adapter is not IDraftingEntityAdapter && !target.Creates && working.Version is { } version && !set.Bases.ContainsKey(ChangeSet.EntityKey(target.EntityType, target.EntityKey)))
            {
                if (adapter is IVersionedEntityAdapter)
                {
                    set.BaseVersions.TryAdd(ChangeSet.EntityKey(target.EntityType, target.EntityKey), version);
                }
                else
                {
                    set.Bases.TryAdd(ChangeSet.EntityKey(target.EntityType, target.EntityKey), working.Clone());
                }
            }

            var schema = await adapter.GetSchemaAsync(working.Subtype, cancellationToken);
            var itemTypes = await registry.ResolveAsync(schema.Root, context.UserKey, cancellationToken);
            var output = await edit.ApplyAsync(working, recorded, new EntityEditContext(adapter, schema, itemTypes), cancellationToken);

            // The user the edit is made for must be allowed to make it, in the cultures it touches.
            var permission = working.Version is null ? EntityPermission.Create : EntityPermission.Update;
            if (await EntityAccess.CheckAsync(adapter, new EntityAccessRequest(context.UserKey, permission, target.EntityKey, working, EntityAccess.VariantsChanged(before, working, schema.Root, itemTypes)), cancellationToken) is { } denied)
            {
                return EntityAccess.Denied(denied);
            }

            // And to use every item type it adds: a plugin's type can need access the entity doesn't.
            if (EntityAccess.NewItemTypesNotAvailable(before, working, schema.Root, itemTypes) is { Count: > 0 } unavailable)
            {
                return EntityAccess.Denied($"You may not use {string.Join(", ", unavailable)}.");
            }
            if (adapter is IDraftingEntityAdapter drafting)
            {
                // The product's draft records the change; the set only remembers that it touched the entity.
                await drafting.StageAsync(await EnsureDraftAsync(set, drafting, cancellationToken), target.Creates ? null : before, working, cancellationToken);
            }

            set.Changes.Add(new RecordedCall(ability.Descriptor.Id, recorded, target.EntityType, target.EntityKey, target.Creates, working.Subtype));
            store.Save(set);
            return AbilityResult.Ok(output);
        }
        catch (AbilityInputException ex)
        {
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, ex.Message, ex.Problems);
        }
        catch (JsonException ex)
        {
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, $"The input does not match the schema: {ex.Message}");
        }
    }

    /// <summary>
    /// The entity as it would be if the set were applied now, or null if it exists neither live nor in the set. The set's
    /// edits are replayed onto the current live version, which rebases them onto anything someone else saved since.
    /// An edit to something they removed is skipped (it shows as a conflict), and conflicts resolved as "theirs" take
    /// the live value.
    /// </summary>
    public async Task<Entity?> GetWorkingStateAsync(ChangeSet set, string entityType, Guid key, CancellationToken cancellationToken = default)
        => (await ComputeAsync(set, entityType, key, cancellationToken)).Working;

    /// <summary>
    /// Paths both the set and someone else changed, differently, since the set started editing each entity, with how
    /// they were resolved. Keyed by entity; an entity that can't be rebased has one conflict at the empty path.
    /// </summary>
    public async Task<IReadOnlyList<RebasedEntity>> GetRebasedAsync(ChangeSet set, CancellationToken cancellationToken = default)
        => (await ComputeAllAsync(set, cancellationToken)).Rebased;

    /// <summary>One entity's state in a set, worked out once: what's live, the set's result, and how it rebased.</summary>
    private sealed record EntityState(string EntityType, Guid Key, IEntityAdapter Adapter, Entity? Live, Entity? Working, RebasedEntity? Rebased)
    {
        public EntitySummary Summary => (Working ?? Live) is { } entity ? Adapter.ToSummary(entity) : Rebased?.Entity ?? new EntitySummary(EntityType, Key, "(deleted)");
    }

    private sealed record SetState(IReadOnlyList<EntityState> Entities, IReadOnlyList<RebasedEntity> Rebased);

    /// <summary>Every pending entity's state, and every rebased entity, including those the products' drafts report.</summary>
    private async Task<SetState> ComputeAllAsync(ChangeSet set, CancellationToken cancellationToken)
    {
        var rebased = new List<RebasedEntity>();
        foreach (var (step, draftId) in set.Drafts.Where(d => !set.AppliedSteps.Contains(d.Key)))
        {
            rebased.AddRange(await DraftingFor(step).GetRebasedAsync(draftId, cancellationToken));
        }

        var entities = new List<EntityState>();
        foreach (var (entityType, key) in PendingEntities(set))
        {
            var state = await ComputeAsync(set, entityType, key, cancellationToken);
            entities.Add(state);
            if (state.Rebased is { } entity)
            {
                rebased.Add(entity);
            }
        }

        return new SetState(entities, rebased);
    }

    private async Task<EntityState> ComputeAsync(ChangeSet set, string entityType, Guid key, CancellationToken cancellationToken)
    {
        var adapter = adapters.Get(entityType);
        var live = await adapter.LoadAsync(key, cancellationToken);
        EntityState State(Entity? working, RebasedEntity? rebased = null) => new(entityType, key, adapter, live, working, rebased);

        if (adapter is IDraftingEntityAdapter drafting)
        {
            // The product's draft holds the working state and reports its own rebase.
            return State(set.Drafts.TryGetValue(adapter.UnitOfWork.Name, out var draftId)
                ? await drafting.LoadDraftAsync(draftId, key, cancellationToken)
                : live?.Clone());
        }

        var calls = set.Changes.Where(c => c.EntityType == entityType && c.EntityKey == key).ToList();
        if (calls.FirstOrDefault() is { Creates: true } create)
        {
            return State(await ReplayAsync(adapter, await adapter.NewAsync(key, create.Subtype, cancellationToken), calls, skipUnder: [], cancellationToken));
        }

        if (calls.Count == 0)
        {
            return State(live?.Clone());
        }

        var start = await GetStartAsync(set, adapter, key, cancellationToken);
        if (live is null)
        {
            return State(null, start is null ? null : CantRebase(adapter.ToSummary(start), "(deleted)"));
        }

        if (start is null || start.Version == live.Version)
        {
            // Nothing changed underneath, or the version the set started from is gone from the product's history.
            var lost = start is null && set.BaseVersions.TryGetValue(ChangeSet.EntityKey(entityType, key), out var version) && live.Version != version;
            return State(
                await ReplayAsync(adapter, live.Clone(), calls, skipUnder: [], cancellationToken),
                lost ? CantRebase(adapter.ToSummary(live), "(unknown: the starting version is no longer in the history)") : null);
        }

        // Someone else saved since: replay the set's edits onto theirs, skipping edits to what they removed.
        var schema = (await adapter.GetSchemaAsync(live.Subtype, cancellationToken)).Root;
        var itemTypes = await registry.ResolveAsync(schema, null, cancellationToken);
        var theirs = EntityDiff.Compare(start.Fields, live.Fields, schema, itemTypes);
        var working = await ReplayAsync(adapter, live.Clone(), calls, theirs.Where(t => t.Structural).ToList(), cancellationToken);
        var yoursEntity = await ReplayAsync(adapter, start.Clone(), calls, skipUnder: [], cancellationToken);
        var yours = EntityDiff.Compare(start.Fields, yoursEntity.Fields, schema, itemTypes);
        var entity = ChangeSet.EntityKey(entityType, key);

        var conflicts = new List<ChangeSetConflict>();
        foreach (var (mine, other) in yours.SelectMany(y => theirs.Where(t => EntityDiff.Overlaps(y, t)).Select(t => (y, t))))
        {
            // Report at the item someone else added or removed, else at the value itself.
            var at = other.Structural ? other : mine;
            var yourValue = EntityDiff.ValueAt(yoursEntity.Fields, schema, itemTypes, at);
            var theirValue = EntityDiff.ValueAt(live.Fields, schema, itemTypes, at);
            if (JsonNode.DeepEquals(yourValue, theirValue) || conflicts.Any(c => c.Path == at.Path && VariantId.From(c.Variant).Equals(at.Variant)))
            {
                continue; // Both made the same change, or it's already listed.
            }

            var resolution = set.Resolutions.TryGetValue(new ConflictKey(entity, at.Path, at.VariantKey), out var side) ? side : (ConflictSide?)null;
            if (resolution == ConflictSide.Theirs)
            {
                EntityDiff.CopyPath(live.Fields, working.Fields, schema, itemTypes, at);
            }

            conflicts.Add(new ChangeSetConflict(adapter.ToSummary(live), at.Path, at.Variant.ToJson(), EntityDiff.ValueAt(start.Fields, schema, itemTypes, at), yourValue, theirValue, resolution));
        }

        return State(working, new RebasedEntity(adapter.ToSummary(live), theirs.Select(Describe).Distinct().ToList(), conflicts));
    }

    /// <summary>An entity that can't be rebased: one conflict at the empty path, resolved only by redoing the changes.</summary>
    private static RebasedEntity CantRebase(EntitySummary entity, string changedSince)
        => new(entity, [changedSince], [new ChangeSetConflict(entity, string.Empty, null, null, null, null, null)]);

    /// <summary>Keeps the set's value or the live one for one conflict.</summary>
    public async Task ResolveConflictAsync(Guid setId, string entityType, Guid key, string path, VariantId variant, ConflictSide keep, CancellationToken cancellationToken = default)
    {
        var set = Get(setId);
        if (adapters.Get(entityType) is IDraftingEntityAdapter drafting
            && set.Drafts.TryGetValue(drafting.UnitOfWork.Name, out var draftId))
        {
            await drafting.ResolveConflictAsync(draftId, key, path, variant, keep, cancellationToken);
            return;
        }

        var conflict = ((await ComputeAsync(set, entityType, key, cancellationToken)).Rebased?.Conflicts ?? [])
            .FirstOrDefault(c => c.Path == path && VariantId.From(c.Variant).Equals(variant))
            ?? throw new AbilityInputException($"No conflict at '{path}' on {entityType} {key}.", [new Problem("path", "No conflict here. List conflicts with changeSet.summarize.")]);

        if (conflict.Path.Length == 0)
        {
            throw new AbilityInputException(
                "This entity can't be rebased: someone else deleted it, or the version the set started from is gone. Discard the set's changes to it and make them again.",
                [new Problem("path", "Can't be rebased.")]);
        }

        if (keep == ConflictSide.Yours && conflict.Theirs is null && conflict.Yours is not null && conflict.Path.Split('/')[^1] is var last && Guid.TryParse(last, out _))
        {
            throw new AbilityInputException("Someone else removed this item, so your edits to it can't be kept. Keep theirs, or add the item again.", [new Problem(path, "Removed by someone else.")]);
        }

        set.Resolutions[new ConflictKey(ChangeSet.EntityKey(entityType, key), path, variant.Key)] = keep;
        store.Save(set);
    }

    /// <summary>
    /// The version the set started editing an entity from: its own snapshot, or, when the product keeps version
    /// history, that version loaded from the history.
    /// </summary>
    private static async Task<Entity?> GetStartAsync(ChangeSet set, IEntityAdapter adapter, Guid key, CancellationToken cancellationToken)
    {
        var entityKey = ChangeSet.EntityKey(adapter.EntityType, key);
        if (set.Bases.TryGetValue(entityKey, out var snapshot))
        {
            return snapshot;
        }

        if (set.BaseVersions.TryGetValue(entityKey, out var version) && adapter is IVersionedEntityAdapter versioned)
        {
            return await versioned.LoadVersionAsync(key, version, cancellationToken);
        }

        return null;
    }

    private async Task<Entity> ReplayAsync(IEntityAdapter adapter, Entity entity, IReadOnlyList<RecordedCall> calls, IReadOnlyList<ChangedPath> skipUnder, CancellationToken cancellationToken)
    {
        if (calls.Count == 0)
        {
            return entity;
        }

        var entitySchema = await adapter.GetSchemaAsync(entity.Subtype, cancellationToken);
        var context = new EntityEditContext(adapter, entitySchema, await registry.ResolveAsync(entitySchema.Root, null, cancellationToken));
        foreach (var call in calls)
        {
            try
            {
                await ((IEntityEditAbility)abilities.Find(call.AbilityId)!).ApplyAsync(entity, call.Input, context, cancellationToken);
            }
            catch (AbilityInputException ex) when (ex.Problems.Any(p => skipUnder.Any(t => p.Path == t.Path || p.Path.StartsWith(t.Path + "/", StringComparison.Ordinal))))
            {
                // The edit was to something someone else removed since; it shows as a conflict instead.
            }
        }

        return entity;
    }

    private static string Describe(ChangedPath change) => change.Variant.IsInvariant ? change.Path : $"{change.Path} ({change.Variant})";

    public async Task<IReadOnlyList<EntityProblems>> ValidateAsync(Guid setId, CancellationToken cancellationToken = default)
    {
        var set = Get(setId);
        return await ValidateAsync(set, await ComputeAllAsync(set, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<EntityProblems>> ValidateAsync(ChangeSet set, SetState state, CancellationToken cancellationToken)
    {
        var result = new List<EntityProblems>();
        foreach (var entity in state.Entities)
        {
            if (entity.Working is not { } working)
            {
                result.Add(new EntityProblems(entity.Summary, [new Problem(string.Empty, "Someone else deleted this since the set started editing it.")]));
                continue;
            }

            var conflicts = state.Rebased
                .Where(r => r.Entity.EntityType == entity.EntityType && r.Entity.Key == entity.Key)
                .SelectMany(r => r.Conflicts)
                .Where(c => c.Resolution is null);

            var problems = new List<Problem>();
            problems.AddRange(conflicts.Select(c => new Problem(
                c.Path,
                c.Path.Length == 0
                    ? "Someone else changed this since the set started editing it, and what they changed can't be worked out: the version the set started from is gone. Discard the set's changes to it and make them again."
                    : $"Someone else changed this since the set started editing it. Yours: {c.Yours?.ToJsonString() ?? "(removed)"}; theirs: {c.Theirs?.ToJsonString() ?? "(removed)"}. Keep one with changeSet.resolveConflict.")
            {
                Variant = c.Variant,
            }));
            var schema = await entity.Adapter.GetSchemaAsync(working.Subtype, cancellationToken);
            var itemTypes = await registry.ResolveAsync(schema.Root, null, cancellationToken);
            problems.AddRange(await SchemaValidator.ValidateAsync(working, schema, itemTypes, (type, k) => ExistsAsync(set, type, k, cancellationToken)));
            problems.AddRange(await entity.Adapter.ValidateAsync(working, cancellationToken));

            // The generic schema check and the product's own validation often find the same problem.
            var distinct = problems.DistinctBy(p => (p.Path, VariantId.From(p.Variant).Key)).ToList();
            if (distinct.Count > 0)
            {
                result.Add(new EntityProblems(entity.Summary, distinct));
            }
        }

        return result;
    }

    public async Task<ChangeSetSummary> SummarizeAsync(Guid setId, CancellationToken cancellationToken = default)
    {
        var set = Get(setId);
        var state = await ComputeAllAsync(set, cancellationToken);
        var changes = new List<ChangeSetChange>();
        foreach (var (entityType, key) in TouchedEntities(set))
        {
            // Entities whose product is already applied aren't pending, so aren't in the set's state.
            var entity = state.Entities.FirstOrDefault(e => e.EntityType == entityType && e.Key == key) ?? await ComputeAsync(set, entityType, key, cancellationToken);
            var calls = set.Changes.Where(c => c.EntityType == entityType && c.EntityKey == key).ToList();
            changes.Add(new ChangeSetChange(entity.Summary, calls[0].Creates ? "created" : "updated", calls.Count));
        }

        var afterApply = set.AfterApply.Select(c => c.AbilityId).ToList();
        var plan = Plan(set).Select(s => new ApplyStep(s.Name, s.Description, set.AppliedSteps.Contains(s.Key))).ToList();
        return new ChangeSetSummary(set.Id, set.Name, changes, afterApply, plan, state.Rebased);
    }

    /// <summary>
    /// Validates the set, including conflicts with anything saved since that are not resolved yet, then applies it one step at a time: each
    /// product's entities in that product's own transaction, then each recorded operation. A failed step rolls back
    /// only itself; earlier steps stay applied and are skipped when the set is applied again.
    /// </summary>
    public async Task<AbilityResult> ApplyAsync(Guid setId, AbilityContext context, CancellationToken cancellationToken = default)
    {
        ChangeSet set;
        try
        {
            set = Get(setId);
        }
        catch (AbilityInputException ex)
        {
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, ex.Message, ex.Problems);
        }

        // First, before validating: the user applying the set must be allowed every change in it: they may not be who made the changes, and
        // permissions can change while a set is open.
        var state = await ComputeAllAsync(set, cancellationToken);
        var denials = new List<Problem>();
        foreach (var entity in state.Entities.Where(e => e.Working is not null))
        {
            var permission = entity.Live is null ? EntityPermission.Create : EntityPermission.Update;
            var schema = (await entity.Adapter.GetSchemaAsync(entity.Working!.Subtype, cancellationToken)).Root;
            var itemTypes = await registry.ResolveAsync(schema, context.UserKey, cancellationToken);
            var request = new EntityAccessRequest(context.UserKey, permission, entity.Key, entity.Working, EntityAccess.VariantsChanged(entity.Live, entity.Working, schema, itemTypes));
            if (await EntityAccess.CheckAsync(entity.Adapter, request, cancellationToken) is { } denied)
            {
                denials.Add(new Problem($"{entity.EntityType}:{entity.Key}", denied));
            }
        }

        if (denials.Count > 0)
        {
            return AbilityResult.Fail(AbilityOperationStatus.AccessDenied, "You may not apply some of the changes in this set. Nothing was applied.", denials);
        }

        var invalid = await ValidateAsync(set, state, cancellationToken);
        if (invalid.Count > 0)
        {
            var problems = invalid.SelectMany(e => e.Problems.Select(p => p with { Path = $"{e.Entity.EntityType}:{e.Entity.Key}/{p.Path}" })).ToList();
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, "The change set has problems. Fix them and apply again.", problems);
        }

        // Packages can veto applying a set, as they can veto saving a document.
        var messages = new EventMessages();
        if (events is not null && await events.PublishCancelableAsync(new ChangeSetApplyingNotification(set, messages)))
        {
            var reasons = string.Join(" ", messages.GetAll().Select(m => m.Message));
            return AbilityResult.Fail(AbilityOperationStatus.Failed, $"Applying was refused. {reasons}".Trim());
        }

        var saved = new List<EntitySummary>();
        var steps = new List<Problem>();
        string? failure = null;
        // The set's changes and side effects run for real, so no change set is current while they do.
        using var noSet = accessor.Use(null);

        foreach (var step in Plan(set))
        {
            if (set.AppliedSteps.Contains(step.Key))
            {
                steps.Add(new Problem(step.Name, "Already applied."));
                continue;
            }

            if (failure is not null)
            {
                steps.Add(new Problem(step.Name, "Not applied yet."));
                continue;
            }

            var outcome = "Applied.";
            try
            {
                if (step.UnitOfWork is { } unitOfWork)
                {
                    await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
                    var entities = state.Entities.Where(e => step.Entities.Contains((e.EntityType, e.Key))).ToList();
                    // Entities a product drafts are applied with its draft, all or nothing. Other adapters can share the
                    // transaction (CMS media shares documents' Umbraco scope): their entities are saved in it too.
                    var drafting = set.Drafts.TryGetValue(step.Key, out var draftId) ? DraftingFor(step.Key) : null;
                    foreach (var entity in entities.Where(e => e.Adapter != drafting))
                    {
                        saved.Add(await entity.Adapter.SaveAsync(entity.Working!, cancellationToken));
                    }

                    if (drafting is not null)
                    {
                        saved.AddRange(entities.Where(e => e.Adapter == drafting).Select(e => e.Summary));
                        await drafting.ApplyDraftAsync(draftId, cancellationToken);
                    }

                    await transaction.CompleteAsync(cancellationToken);
                }
                else if (abilities.Find(step.Call!.AbilityId)! is { Descriptor.Retries: > 0 } retried && background is not null)
                {
                    QueueSideEffect(set, retried, step.Call, context);
                    outcome = $"Queued: runs in the background, retried up to {retried.Descriptor.Retries} times.";
                }
                else
                {
                    var result = await abilities.Find(step.Call!.AbilityId)!.InvokeAsync(step.Call.Input, context, cancellationToken);
                    if (!result.Success)
                    {
                        throw new InvalidOperationException(result.Message);
                    }
                }

                set.AppliedSteps.Add(step.Key);
                store.Save(set);
                steps.Add(new Problem(step.Name, outcome));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A product's changes roll back with its transaction; a side effect has nothing to roll back.
                var failed = step.UnitOfWork is not null ? "failed and was rolled back" : "failed";
                failure = $"{step.Name} {failed}: {ex.Message}";
                steps.Add(new Problem(step.Name, $"{char.ToUpperInvariant(failed[0])}{failed[1..]}: {ex.Message}"));
            }
        }

        if (failure is not null)
        {
            var appliedSome = set.AppliedSteps.Count > 0;
            return AbilityResult.Fail(
                AbilityOperationStatus.Failed,
                appliedSome
                    ? $"Partly applied. {failure} Earlier steps stay applied. Fix the cause and apply again to apply the rest; nothing is applied twice."
                    : $"Nothing was applied. {failure}",
                steps);
        }

        store.Remove(set.Id);
        if (events is not null)
        {
            await events.PublishAsync(new ChangeSetAppliedNotification(set, saved), cancellationToken);
        }

        return AbilityResult.Ok(saved);
    }

    /// <summary>
    /// The steps of applying a set: one per product transaction, in the order the set first touched them, then each
    /// side effect in the order it was recorded, once every change is saved.
    /// </summary>
    private IReadOnlyList<PlannedStep> Plan(ChangeSet set)
    {
        var steps = TouchedEntities(set)
            .GroupBy(e => adapters.Get(e.EntityType).UnitOfWork.Name)
            .Select(g => new PlannedStep(
                g.Key,
                g.Key,
                $"{g.Count()} {(g.Count() == 1 ? "entity" : "entities")}, saved together: all or nothing.",
                adapters.Get(g.First().EntityType).UnitOfWork,
                g.ToList(),
                null))
            .ToList();

        steps.AddRange(set.AfterApply.Select((call, index) => new PlannedStep(
            $"after:{index}", call.AbilityId, "Side effect: runs after every change is applied, and can't be undone.", null, [], call)));
        return steps;
    }

    private sealed record PlannedStep(
        string Key,
        string Name,
        string Description,
        IUnitOfWorkProvider? UnitOfWork,
        IReadOnlyList<(string EntityType, Guid Key)> Entities,
        RecordedCall? Call);

    /// <summary>Entities whose product's changes are not applied yet.</summary>
    private IEnumerable<(string EntityType, Guid Key)> PendingEntities(ChangeSet set)
        => TouchedEntities(set).Where(e => !set.AppliedSteps.Contains(adapters.Get(e.EntityType).UnitOfWork.Name));

    private async Task<bool> ExistsAsync(ChangeSet set, string entityType, Guid key, CancellationToken cancellationToken)
        => adapters.Find(entityType) is not null && await GetWorkingStateAsync(set, entityType, key, cancellationToken) is not null;

    private static IEnumerable<(string EntityType, Guid Key)> TouchedEntities(ChangeSet set)
        => set.Changes.Select(c => (c.EntityType!, c.EntityKey!.Value)).Distinct();

    /// <summary>
    /// Queues a side effect on Umbraco's background task queue, retried with Polly: exponential backoff with jitter, and
    /// only for failures trying again can fix. The outcome is a notification, since the set is applied by then. The queue
    /// is in memory, so work still queued when the site restarts is lost.
    /// </summary>
    private void QueueSideEffect(ChangeSet set, IAbility ability, RecordedCall call, AbilityContext context)
    {
        var pipeline = new ResiliencePipelineBuilder<AbilityResult>()
            .AddRetry(new RetryStrategyOptions<AbilityResult>
            {
                MaxRetryAttempts = ability.Descriptor.Retries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = retryDelay ?? TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder<AbilityResult>()
                    .Handle<Exception>(e => e is not OperationCanceledException)
                    .HandleResult(r => r.Status == AbilityOperationStatus.Failed),
            })
            .Build();

        background!.QueueBackgroundWorkItem(async cancellationToken =>
        {
            var attempts = 0;
            var result = await pipeline.ExecuteAsync(
                async token =>
                {
                    attempts++;
                    return await ability.InvokeAsync(call.Input, context, token);
                },
                cancellationToken);

            if (events is not null)
            {
                await events.PublishAsync(new ChangeSetSideEffectCompletedNotification(set, ability.Descriptor.Id, result, attempts), cancellationToken);
            }
        });
    }

    private async Task<Guid> EnsureDraftAsync(ChangeSet set, IDraftingEntityAdapter drafting, CancellationToken cancellationToken)
    {
        if (!set.Drafts.TryGetValue(drafting.UnitOfWork.Name, out var draftId))
        {
            draftId = await drafting.CreateDraftAsync(set.Name, cancellationToken);
            set.Drafts[drafting.UnitOfWork.Name] = draftId;
        }

        return draftId;
    }

    private IDraftingEntityAdapter DraftingFor(string step)
        => adapters.All.OfType<IDraftingEntityAdapter>().First(d => d.UnitOfWork.Name == step);
}

/// <summary>
/// The single entry point products call. A Write ability called with an active change set is recorded instead of run.
/// </summary>
public sealed class AbilityInvoker(AbilityRegistry abilities, ChangeSetService changeSets)
{
    /// <summary>The change sets calls are recorded into, for surfaces that choose the current one.</summary>
    public ChangeSetService ChangeSets => changeSets;

    public async Task<AbilityResult> InvokeAsync(string abilityId, JsonObject input, AbilityContext context, CancellationToken cancellationToken = default)
    {
        var ability = abilities.Find(abilityId);
        if (ability is null)
        {
            return AbilityResult.Fail(AbilityOperationStatus.InvalidInput, $"Unknown ability '{abilityId}'.");
        }

        if (changeSets.CurrentId is { } setId
            && ability.Descriptor.Kind == AbilityKind.Write
            && ability is not IChangeSetLifecycleAbility)
        {
            return await changeSets.RecordAsync(setId, ability, input, context, cancellationToken);
        }

        return await ability.InvokeAsync(input, context, cancellationToken);
    }
}
