using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>Products that keep their own drafts, sharing one transaction: each keeps its own draft in a change set.</summary>
public class DraftingTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly ScriptedAgent _agent;
    private readonly NoteAdapter _notes;
    private readonly NoteAdapter _memos;

    public DraftingTests()
    {
        // Two products with drafts, saved in one transaction, as CMS documents and a second drafting CMS type would be.
        var database = new InMemoryDatabase("Notes");
        _notes = new NoteAdapter("note", database);
        _memos = new NoteAdapter("memo", database);
        _site.Adapters.Add(_notes).Add(_memos);
        _agent = new ScriptedAgent(_site);
    }

    [Fact]
    public async Task Two_products_with_drafts_in_one_transaction_each_keep_their_own_draft_and_both_are_applied()
    {
        await _agent.StartChangeSetAsync("Notes");
        var note = (await _agent.OkAsync("entity_create", """{ "entityType": "note", "fields": { "text": "Buy milk" } }""")).GetProperty("key").GetGuid();
        var memo = (await _agent.OkAsync("entity_create", """{ "entityType": "memo", "fields": { "text": "Team lunch" } }""")).GetProperty("key").GetGuid();

        // One draft each, not one shared by the transaction.
        var set = _site.ChangeSets.Get(_agent.ChangeSetId!.Value);
        Assert.Equal(["memo", "note"], set.Drafts.Keys.Order());
        Assert.Equal(note, Assert.Single(_notes.Drafts[set.Drafts["note"]]).Key);
        Assert.Equal(memo, Assert.Single(_memos.Drafts[set.Drafts["memo"]]).Key);

        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.Equal("Buy milk", _notes.Live[note]);
        Assert.Equal("Team lunch", _memos.Live[memo]);
    }

    [Fact]
    public async Task Discarding_a_set_discards_every_products_draft()
    {
        await _agent.StartChangeSetAsync("Notes");
        await _agent.OkAsync("entity_create", """{ "entityType": "note", "fields": { "text": "Buy milk" } }""");
        await _agent.OkAsync("entity_create", """{ "entityType": "memo", "fields": { "text": "Team lunch" } }""");
        var set = _site.ChangeSets.Get(_agent.ChangeSetId!.Value);

        await _agent.OkAsync("changeSet_discard", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.Equal([set.Drafts["note"]], _notes.Discarded);
        Assert.Equal([set.Drafts["memo"]], _memos.Discarded);
    }

    /// <summary>A product with drafts of its own, in memory: entities with one text field.</summary>
    private sealed class NoteAdapter(string entityType, IUnitOfWorkProvider unitOfWork) : IDraftingEntityAdapter, IAuthorizingEntityAdapter
    {
        private static readonly FieldDefinition Text = new("text", "The text.", FieldType.String);

        public Dictionary<Guid, string> Live { get; } = [];

        public Dictionary<Guid, List<Entity>> Drafts { get; } = [];

        public List<Guid> Discarded { get; } = [];

        public string EntityType => entityType;

        public bool CanCreate => true;

        public IUnitOfWorkProvider UnitOfWork => unitOfWork;

        public Task<IReadOnlyList<EntitySubtype>> GetSubtypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EntitySubtype>>([]);

        public Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default)
            => Task.FromResult(new EntitySchema(entityType, entityType, $"A {entityType}.", ObjectSchema.Of(Text)));

        public Task<Entity> NewAsync(Guid key, string? subtype, CancellationToken cancellationToken = default)
            => Task.FromResult(new Entity { EntityType = entityType, Key = key, Subtype = subtype });

        public Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default)
            => Task.FromResult(Live.TryGetValue(key, out var text) ? new Entity { EntityType = entityType, Key = key, Version = 1, Fields = { ["text"] = text } } : null);

        public Task<IReadOnlyList<Problem>> ValidateAsync(Entity entity, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Problem>>([]);

        public Task<EntitySummary> SaveAsync(Entity? before, Entity entity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Applied through the draft.");

        public Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EntitySummary>>([]);

        public EntitySummary ToSummary(Entity entity) => new(entityType, entity.Key, entity.Fields["text"]?.ToString() ?? "(empty)", string.Empty);

        public Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<Guid> CreateDraftAsync(string name, CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            Drafts[id] = [];
            return Task.FromResult(id);
        }

        public async Task<Entity?> LoadDraftAsync(Guid draftId, Guid key, CancellationToken cancellationToken = default)
            => Drafts[draftId].FirstOrDefault(e => e.Key == key)?.Clone() ?? await LoadAsync(key, cancellationToken);

        public Task StageAsync(Guid draftId, Entity? before, Entity after, CancellationToken cancellationToken = default)
        {
            Drafts[draftId].RemoveAll(e => e.Key == after.Key);
            Drafts[draftId].Add(after.Clone());
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RebasedEntity>> GetRebasedAsync(Guid draftId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RebasedEntity>>([]);

        public Task ResolveConflictAsync(Guid draftId, Guid key, string path, VariantId variant, ConflictSide keep, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApplyDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        {
            foreach (var entity in Drafts[draftId])
            {
                Live[entity.Key] = entity.Fields["text"]!.ToString();
            }

            return Task.CompletedTask;
        }

        public Task DiscardDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        {
            Discarded.Add(draftId);
            return Task.CompletedTask;
        }
    }
}
