using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.DraftSets;

namespace Umbraco.Abilities.Cms;

// The adapter to CMS Draft Sets: inside a change set, content edits are staged into a draft set and applied with it.
public sealed partial class CmsDocumentAdapter
{
    public Task<Guid> CreateDraftAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_draftSets.Create(name).Id);

    public async Task<Entity?> LoadDraftAsync(Guid draftId, Guid key, CancellationToken cancellationToken = default)
        => await _draftSets.GetAsync(draftId, EntityUdis.For(Type, key)) is { } content ? await ToEntityAsync(Stored(content), cancellationToken) : null;

    /// <summary>
    /// Stages an edit. New content is staged whole. For existing content only the values the edit changed are staged,
    /// per property, culture and segment, so the draft set can rebase onto values others change meanwhile.
    /// </summary>
    public async Task StageAsync(Guid draftId, Entity? before, Entity after, CancellationToken cancellationToken = default)
    {
        if (after.Deleted)
        {
            await _draftSets.StageDeleteAsync(draftId, EntityUdis.For(Type, after.Key));
            return;
        }

        var content = await ToContentAsync(after, cancellationToken);
        var values = content.Values.Select(v => new DraftValue(v.Alias, v.Culture, v.Segment, v.Value)).ToList();
        if (before is null || before.Version is null)
        {
            _draftSets.StageNew(draftId, new DraftContent
            {
                Key = content.Key,
                ContentTypeKey = content.TypeKey,
                ParentKey = content.ParentKey,
                Names = content.Variants.Where(v => v.Segment is null).ToDictionary(v => v.Culture ?? string.Empty, v => v.Name),
                Values = values,
            });
            return;
        }

        var changed = (await TouchedValuesAsync(before, after, cancellationToken)).Select(t => t.Alias == ContentFields.Name
            ? new DraftValue(DraftSetService.NameAlias, t.Culture, null, content.Variants.FirstOrDefault(v => v.Culture == t.Culture && v.Segment is null)?.Name is { } name ? JsonValue.Create(name) : null)
            : values.FirstOrDefault(v => v.Alias == t.Alias && v.Culture == t.Culture && v.Segment == t.Segment) ?? new DraftValue(t.Alias, t.Culture, t.Segment, null)).ToList();

        // A new parent is a move, staged as the draft set's parent value.
        if (!JsonNode.DeepEquals(before.Fields[ContentFields.Parent], after.Fields[ContentFields.Parent]))
        {
            changed.Add(new DraftValue(DraftSetService.ParentAlias, null, null, content.ParentKey is { } parent ? JsonValue.Create(parent.ToString()) : null));
        }

        await _draftSets.StageChangesAsync(draftId, EntityUdis.For(Type, after.Key), changed);
    }

    public async Task<IReadOnlyList<RebasedEntity>> GetRebasedAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        var result = new List<RebasedEntity>();
        foreach (var rebase in await _draftSets.GetRebaseAsync(draftId))
        {
            var live = await LoadAsync(rebase.Entity.Guid, cancellationToken);
            var entity = live is not null ? ToSummary(live) : new EntitySummary(Type, rebase.Entity.Guid, "(deleted)", EditUrl(rebase.Entity.Guid));
            if (rebase.DeletedLive)
            {
                result.Add(new RebasedEntity(entity, ["(deleted)"], [new ChangeSetConflict(entity, string.Empty, null, null, null, null, null)]));
                continue;
            }

            result.Add(new RebasedEntity(
                entity,
                rebase.ChangedSince.Select(c => c.Culture is null ? PathOf(c.Alias) : $"{PathOf(c.Alias)} ({c.Culture})").Distinct().ToList(),
                rebase.Conflicts.Select(c => new ChangeSetConflict(entity, PathOf(c.Alias), CmsVariants.Of(c.Culture, c.Segment).ToJson(), c.Base, c.Draft, c.Live, null)).ToList()));
        }

        return result;
    }

    public Task ResolveConflictAsync(Guid draftId, Guid key, string path, VariantId variant, ConflictSide keep, CancellationToken cancellationToken = default)
        => _draftSets.ResolveAsync(
            draftId,
            EntityUdis.For(Type, key),
            path switch
            {
                ContentFields.Name => DraftSetService.NameAlias,
                ContentFields.Parent => DraftSetService.ParentAlias,
                _ => path,
            },
            variant.Culture,
            variant.Segment,
            keep == ConflictSide.Yours ? DraftKeep.Draft : DraftKeep.Live);

    public Task ApplyDraftAsync(Guid draftId, CancellationToken cancellationToken = default) => _draftSets.ApplyAsync(draftId, _actingUser.Key);

    public Task DiscardDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        _draftSets.Discard(draftId);
        return Task.CompletedTask;
    }

    private static string PathOf(string alias) => alias switch
    {
        DraftSetService.NameAlias => ContentFields.Name,
        DraftSetService.ParentAlias => ContentFields.Parent,
        _ => alias,
    };

    private static StoredContent Stored(DraftContent content)
        => new(content.Key, content.ContentTypeKey, content.Names, content.Values.Select(v => new StoredValue(v.Alias, v.Culture, v.Segment, v.Value)).ToList(), content.Version, content.ParentKey);
}
