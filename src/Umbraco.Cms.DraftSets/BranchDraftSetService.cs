using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.ContentEditing.Validation;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Cms.DraftSets;

/// <summary>
/// A draft set built on named draft branches (<see cref="ContentDraftBranchService"/>): what an umbrella Draft Sets
/// feature would add on top of PR #24784. It does what <see cref="DraftSetService"/> does, with one branch per document
/// it edits holding the edits:
/// <list type="bullet">
///   <item>Edits to existing content are saved to that document's branch, named after the set.</item>
///   <item>New content, moves and deletes are kept by the set and run when it is applied. A branch doesn't move or delete.</item>
///   <item>Rebase and conflicts compare the branch's base, the branch and the default draft.</item>
///   <item>Applying promotes every branch, creates, moves and deletes in one scope: all or nothing.</item>
///   <item>Discarding abandons the branches.</item>
/// </list>
/// </summary>
public sealed class BranchDraftSetService(
    ContentDraftBranchService branches,
    IContentEditingService editing,
    PropertyEditorCollection editors,
    IJsonSerializer json,
    ICoreScopeProvider scopes,
    IEventAggregator events,
    IAuditService audit,
    IIdKeyMap idKeys) : IDraftSetService
{
    private static readonly string ParentId = new DraftValue(DraftSetService.ParentAlias, null, null, null).Id;

    private readonly EditorContent _content = new(editing, editors, json, idKeys);

    private readonly ConcurrentDictionary<Guid, DraftSet> _sets = new();

    public DraftSet Create(string name)
    {
        var set = new DraftSet { Name = name };
        _sets[set.Id] = set;
        return set;
    }

    public DraftSet? Get(Guid id) => _sets.GetValueOrDefault(id);

    public void Discard(Guid id)
    {
        if (_sets.TryRemove(id, out var set))
        {
            foreach (var (entity, entry) in set.Entries)
            {
                AbandonBranch(entity, entry);
            }

            events.Publish(new DraftSetDiscardedNotification(set));
        }
    }

    // ── Recording ───────────────────────────────────────────────────────────────────────────────

    public void StageNew(Guid id, DraftContent content)
    {
        var set = Open(id);
        set.Entries[content.Udi] = new DraftEntry { Created = content.Clone() };
    }

    public async Task StageChangesAsync(Guid id, GuidUdi entity, IEnumerable<DraftValue> values)
    {
        var set = Open(id);
        var entry = set.Entries.GetValueOrDefault(entity) ?? await BranchAsync(set, entity);
        if (entry.Created is { } created)
        {
            foreach (var value in values)
            {
                EditorContent.Overlay(created, value);
            }

            return;
        }

        var key = entity.Guid;
        var content = branches.GetContent(key, entry.BranchKey!.Value)!;
        foreach (var value in values)
        {
            // A move is the set's, not the branch's: the set runs it when applied.
            if (value.Alias == DraftSetService.ParentAlias)
            {
                if (JsonNode.DeepEquals(EditorContent.ValueIn(entry.Base!, value), value.Value))
                {
                    entry.Changes.Remove(value.Id);
                }
                else
                {
                    entry.Changes[value.Id] = value with { Value = value.Value?.DeepClone() };
                }

                continue;
            }

            EditorContent.Overlay(content, value);
        }

        branches.Save(key, entry.BranchKey!.Value, content);
    }

    public async Task StageDeleteAsync(Guid id, GuidUdi entity)
    {
        var set = Open(id);
        if (set.Entries.TryGetValue(entity, out var existing) && existing.Created is not null)
        {
            set.Entries.Remove(entity);
            return;
        }

        existing ??= set.Entries[entity] = new DraftEntry { Base = await _content.GetLiveAsync(entity) ?? throw new InvalidOperationException($"No {entity}.") };

        // A deleted document's edits are dropped: its branch goes.
        AbandonBranch(entity, existing);
        existing.BranchKey = null;
        existing.Changes.Clear();
        existing.Deleted = true;
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────────

    public Task<DraftContent?> GetLiveAsync(GuidUdi entity) => _content.GetLiveAsync(entity);

    public async Task<DraftContent?> GetAsync(Guid id, GuidUdi entity)
    {
        var set = _sets.GetValueOrDefault(id);
        if (set is null || set.Applied || !set.Entries.TryGetValue(entity, out var entry))
        {
            return await GetLiveAsync(entity);
        }

        if (entry.Created is { } created)
        {
            return created.Clone();
        }

        if (entry.Deleted || await GetLiveAsync(entity) is not { } live)
        {
            return null;
        }

        foreach (var change in Changes(entity, entry))
        {
            EditorContent.Overlay(live, change);
        }

        return live;
    }

    // ── Checking ────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DraftRebase>> GetRebaseAsync(Guid id)
    {
        var result = new List<DraftRebase>();
        foreach (var (key, entry) in Open(id).Entries.Where(e => e.Value.Base is not null))
        {
            var live = await GetLiveAsync(key);
            if (live is null)
            {
                if (!entry.Deleted)
                {
                    result.Add(new DraftRebase(key, DeletedLive: true, [], []));
                }

                continue;
            }

            if (live.Version == entry.Base!.Version)
            {
                continue;
            }

            var conflicts = new List<DraftConflict>();
            foreach (var change in Changes(key, entry))
            {
                var original = EditorContent.ValueIn(entry.Base, change);
                var theirs = EditorContent.ValueIn(live, change);
                var keptOver = entry.KeptOver.TryGetValue(change.Id, out var kept) && JsonNode.DeepEquals(kept, theirs);
                if (!JsonNode.DeepEquals(original, theirs) && !JsonNode.DeepEquals(change.Value, theirs) && !keptOver)
                {
                    conflicts.Add(new DraftConflict(change.Alias, change.Culture, change.Segment, original, change.Value, theirs));
                }
            }

            result.Add(new DraftRebase(key, DeletedLive: false, EditorContent.Diff(entry.Base, live).ToList(), conflicts));
        }

        return result;
    }

    public async Task ResolveAsync(Guid id, GuidUdi entity, string alias, string? culture, string? segment, DraftKeep keep)
    {
        var entry = Open(id).Entries.GetValueOrDefault(entity) ?? throw new InvalidOperationException($"The draft set has no changes to {entity}.");
        var change = new DraftValue(alias, culture, segment, null);
        if (keep == DraftKeep.Draft)
        {
            entry.KeptOver[change.Id] = EditorContent.ValueIn((await GetLiveAsync(entity))!, change)?.DeepClone();
            return;
        }

        if (alias == DraftSetService.ParentAlias)
        {
            entry.Changes.Remove(change.Id);
            return;
        }

        // Keep theirs: the branch's value goes back to its base, so it is no longer a change and promoting keeps theirs.
        var content = branches.GetContent(entity.Guid, entry.BranchKey!.Value)!;
        EditorContent.Overlay(content, change with { Value = EditorContent.ValueIn(entry.Base!, change)?.DeepClone() });
        branches.Save(entity.Guid, entry.BranchKey.Value, content);
    }

    public async Task<IReadOnlyDictionary<GuidUdi, IReadOnlyList<PropertyValidationError>>> ValidateAsync(Guid id, Guid userKey)
    {
        var result = new Dictionary<GuidUdi, IReadOnlyList<PropertyValidationError>>();
        foreach (var entity in Open(id).Entries.Where(e => !e.Value.Deleted).Select(e => e.Key))
        {
            result[entity] = await _content.ValidateAsync((await GetAsync(id, entity))!, userKey);
        }

        return result;
    }

    // ── Finishing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the set's new content and promotes its branches, then moves, then deletes, in one scope: all or nothing.
    /// Each promote joins this scope, so a later failure rolls back the promotes before it and keeps their branches.
    /// </summary>
    public async Task ApplyAsync(Guid id, Guid userKey)
    {
        var set = Open(id);
        var messages = new EventMessages();
        if (await events.PublishCancelableAsync(new DraftSetApplyingNotification(set, messages)))
        {
            throw new InvalidOperationException($"Applying draft set '{set.Name}' was refused. {string.Join(" ", messages.GetAll().Select(m => m.Message))}".Trim());
        }

        using var scope = scopes.CreateCoreScope();
        var moves = new List<DraftContent>();
        foreach (var (entity, entry) in set.Entries.Where(e => !e.Value.Deleted))
        {
            var content = await GetAsync(id, entity)
                ?? throw new InvalidOperationException($"Umbraco did not save {entry.Base!.Names.Values.FirstOrDefault() ?? entity.Guid.ToString()}: it was deleted.");
            IContent? saved = null;
            if (entry.Created is not null)
            {
                saved = await _content.SaveAsync(content, userKey);
            }
            else
            {
                if (entry.Changes.ContainsKey(ParentId))
                {
                    moves.Add(content);
                }

                if (Changes(entity, entry).Any(c => c.Alias != DraftSetService.ParentAlias))
                {
                    if (!await branches.PromoteAsync(entity.Guid, entry.BranchKey!.Value, userKey))
                    {
                        throw new InvalidOperationException($"Promoting the draft branch of {content.Names.Values.FirstOrDefault() ?? entity.Guid.ToString()} was refused.");
                    }

                    saved = (await editing.GetAsync(entity.Guid))!;
                }
            }

            if (saved is not null)
            {
                await audit.AddAsync(AuditType.Custom, userKey, saved.Id, "Document", $"Applied with draft set '{set.Name}'", null);
            }
        }

        foreach (var content in moves)
        {
            var moved = await editing.MoveAsync(content.Key, content.ParentKey, userKey);
            if (!moved.Success)
            {
                throw new InvalidOperationException($"Umbraco did not move {content.Names.Values.FirstOrDefault() ?? content.Key.ToString()}: {moved.Status}.");
            }

            await audit.AddAsync(AuditType.Custom, userKey, moved.Result!.Id, "Document", $"Moved with draft set '{set.Name}'", null);
        }

        foreach (var (entity, entry) in set.Entries.Where(e => e.Value.Deleted))
        {
            var deleted = await editing.MoveToRecycleBinAsync(entity.Guid, userKey);
            if (!deleted.Success)
            {
                throw new InvalidOperationException($"Umbraco did not delete {entry.Base!.Names.Values.FirstOrDefault() ?? entity.Guid.ToString()}: {deleted.Status}.");
            }

            await audit.AddAsync(AuditType.Custom, userKey, deleted.Result!.Id, "Document", $"Moved to the recycle bin with draft set '{set.Name}'", null);
        }

        scope.Notifications.Publish(new DraftSetAppliedNotification(set));
        scopes.Context!.Enlist($"draft-set-applied:{set.Id}", committed =>
        {
            if (committed)
            {
                set.Applied = true;

                // Branches with nothing to promote (only moved) go too.
                foreach (var (entity, entry) in set.Entries)
                {
                    AbandonBranch(entity, entry);
                }
            }
        });
        scope.Complete();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private DraftSet Open(Guid id)
    {
        var set = _sets.GetValueOrDefault(id) ?? throw new InvalidOperationException($"No draft set {id}.");
        return set.Applied ? throw new InvalidOperationException($"Draft set {id} is already applied.") : set;
    }

    /// <summary>The first edit to existing content makes its branch, named after the set.</summary>
    private async Task<DraftEntry> BranchAsync(DraftSet set, GuidUdi entity)
    {
        var branch = await branches.CreateAsync(EditorContent.DocumentKey(entity), set.Name) ?? throw new InvalidOperationException($"No {entity}.");
        var entry = new DraftEntry { Base = branches.GetBase(branch.ContentKey, branch.Key), BranchKey = branch.Key };
        set.Entries[entity] = entry;
        return entry;
    }

    /// <summary>What the set changes: the values its branch changed since its base, and the set's own move.</summary>
    private IEnumerable<DraftValue> Changes(GuidUdi entity, DraftEntry entry)
    {
        var edits = entry.BranchKey is { } branchKey && branches.GetContent(entity.Guid, branchKey) is { } content
            ? EditorContent.Diff(entry.Base!, content).Where(c => c.Alias != DraftSetService.ParentAlias)
            : [];
        return edits.Concat(entry.Changes.Values);
    }

    private void AbandonBranch(GuidUdi entity, DraftEntry entry)
    {
        if (entry.BranchKey is { } branchKey)
        {
            branches.Abandon(entity.Guid, branchKey);
        }
    }
}
