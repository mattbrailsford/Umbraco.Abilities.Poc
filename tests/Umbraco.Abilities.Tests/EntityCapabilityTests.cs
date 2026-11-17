using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// What an entity type can do beyond setting values: delete and move, which the framework offers when an adapter opts
/// in, and capabilities another package ships the same way, with its own adapter interface.
/// </summary>
public class EntityCapabilityTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly FolderAdapter _folders = new();
    private readonly ScriptedAgent _agent;

    public EntityCapabilityTests()
    {
        _site.Adapters.Add(_folders);

        // Another package's capability. It knows nothing about folders; folders opt in by implementing its interface.
        _site.Abilities.Add(new ArchiveEntityAbility());
        _agent = new ScriptedAgent(_site);
    }

    [Fact]
    public async Task The_schema_lists_what_an_entity_type_can_do_beyond_setting_values()
    {
        var folders = await _agent.OkAsync("entity_getSchema", """{ "entityType": "folder" }""");
        var discounts = await _agent.OkAsync("entity_getSchema", """{ "entityType": "umbraco-commerce-discount" }""");

        Assert.Equal(["archive", "delete", "move"], folders.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.Empty(discounts.GetProperty("capabilities").EnumerateArray());
    }

    [Fact]
    public async Task A_deleted_entity_is_gone_in_the_set_and_deleted_only_when_the_set_is_applied()
    {
        var docs = _folders.Seed("Docs");

        await _agent.StartChangeSetAsync("Tidy up");
        var deleted = await _agent.OkAsync("entity_delete", $$"""{ "entityType": "folder", "key": "{{docs}}" }""");
        Assert.False(deleted.GetProperty("canRestore").GetBoolean());

        // Gone in the set, still live outside it.
        await _agent.FailAsync("entity_get", $$"""{ "entityType": "folder", "key": "{{docs}}" }""");
        Assert.Empty((await _agent.OkAsync("entity_find", """{ "entityType": "folder", "query": "Docs" }""")).EnumerateArray());
        Assert.True(_folders.Live.ContainsKey(docs));

        // Nothing more can be done to it in the set.
        await _agent.FailAsync("entity_setValue", $$"""{ "entityType": "folder", "key": "{{docs}}", "path": "name", "value": "Again" }""");

        var summary = await _agent.OkAsync("changeSet_summarize", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.Equal("deleted", summary.GetProperty("entities")[0].GetProperty("change").GetString());

        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.False(_folders.Live.ContainsKey(docs));
    }

    [Fact]
    public async Task An_entity_created_and_deleted_in_one_set_is_never_saved()
    {
        await _agent.StartChangeSetAsync("Change of mind");
        var key = (await _agent.OkAsync("entity_create", """{ "entityType": "folder", "fields": { "name": "Temp" } }""")).GetProperty("key").GetGuid();
        await _agent.OkAsync("entity_delete", $$"""{ "entityType": "folder", "key": "{{key}}" }""");

        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.Empty(_folders.Live);
        Assert.Empty(_folders.Deleted);
    }

    [Fact]
    public async Task Children_moved_out_of_a_deleted_parent_are_saved_before_it_is_deleted()
    {
        var old = _folders.Seed("Old");
        var child = _folders.Seed("Child", old);

        await _agent.StartChangeSetAsync("Flatten");
        var moved = await _agent.OkAsync("entity_move", $$"""{ "entityType": "folder", "key": "{{child}}" }""");
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("parent").ValueKind);
        await _agent.OkAsync("entity_delete", $$"""{ "entityType": "folder", "key": "{{old}}" }""");

        // Inside the set the child is at the root already.
        var read = await _agent.OkAsync("entity_get", $$"""{ "entityType": "folder", "key": "{{child}}" }""");
        Assert.False(read.GetProperty("fields").TryGetProperty("parent", out _));

        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.Equal(["save Child", "delete Old"], _folders.Log);
        Assert.Null(_folders.Live[child]["parent"]);
    }

    [Fact]
    public async Task Entity_types_whose_adapters_did_not_opt_in_cannot_be_deleted_or_moved()
    {
        await _agent.StartChangeSetAsync("Nope");
        var deleted = await _agent.FailAsync("entity_delete", $$"""{ "entityType": "umbraco-commerce-collection", "key": "{{_site.SummerCollection}}" }""");
        var moved = await _agent.FailAsync("entity_move", $$"""{ "entityType": "umbraco-commerce-collection", "key": "{{_site.SummerCollection}}" }""");

        Assert.Equal("entityType", deleted.GetProperty("problems")[0].GetProperty("path").GetString());
        Assert.Equal("entityType", moved.GetProperty("problems")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_move_is_checked_as_a_move_and_setting_the_parent_directly_is_too()
    {
        var a = _folders.Seed("A");
        var b = _folders.Seed("B");
        var child = _folders.Seed("Child", a);

        await _agent.StartChangeSetAsync("Moves");
        await _agent.OkAsync("entity_move", $$"""{ "entityType": "folder", "key": "{{child}}", "parent": "{{b}}" }""");
        Assert.Equal([EntityPermission.Move], _folders.Checked.Select(r => r.Permission));

        // The same change through setValue takes the same permission, so it can't be used to get round it.
        _folders.Checked.Clear();
        await _agent.OkAsync("entity_setValue", $$"""{ "entityType": "folder", "key": "{{child}}", "path": "parent", "value": "{{a}}" }""");
        Assert.Equal([EntityPermission.Move], _folders.Checked.Select(r => r.Permission));

        // Refused when the product says no.
        _folders.Refuse = EntityPermission.Move;
        var refused = await _agent.FailAsync("entity_move", $$"""{ "entityType": "folder", "key": "{{child}}", "parent": "{{b}}" }""");
        Assert.Equal("AccessDenied", refused.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_entity_cannot_be_moved_under_itself()
    {
        var a = _folders.Seed("A");

        await _agent.StartChangeSetAsync("Loop");
        var refused = await _agent.FailAsync("entity_move", $$"""{ "entityType": "folder", "key": "{{a}}", "parent": "{{a}}" }""");
        Assert.Equal("parent", refused.GetProperty("problems")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_capability_another_package_ships_works_on_any_entity_type_whose_adapter_implements_its_interface()
    {
        var docs = _folders.Seed("Docs");

        await _agent.StartChangeSetAsync("Archive");
        await _agent.OkAsync("entity_archive", $$"""{ "entityType": "folder", "key": "{{docs}}" }""");
        var read = await _agent.OkAsync("entity_get", $$"""{ "entityType": "folder", "key": "{{docs}}" }""");
        Assert.True(read.GetProperty("fields").GetProperty("hidden").GetBoolean());

        // It only set a value, so it is checked as an update and saved like any edit.
        Assert.Equal([EntityPermission.Update], _folders.Checked.Select(r => r.Permission));
        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.True(_folders.Live[docs]["hidden"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_capability_refuses_an_entity_type_whose_adapter_does_not_implement_its_interface()
    {
        await _agent.StartChangeSetAsync("Archive");
        var refused = await _agent.FailAsync("entity_archive", $$"""{ "entityType": "umbraco-commerce-collection", "key": "{{_site.SummerCollection}}" }""");

        Assert.Equal("entityType", refused.GetProperty("problems")[0].GetProperty("path").GetString());
        Assert.Contains("doesn't support archive", refused.GetProperty("message").GetString());
    }

    // ── Another package's capability ──────────────────────────────────────────────────────────

    /// <summary>The interface the archive package ships. A product implements it to say which field hides an entity.</summary>
    public interface IArchivableEntityAdapter : IEntityAdapter
    {
        string ArchivedField { get; }
    }

    public sealed record ArchiveInput(
        [property: Description("Entity type.")] string EntityType,
        [property: Description("Key of the entity to archive.")] Guid? Key) : IEntityEditInput;

    /// <summary>Hides an entity, on any entity type whose adapter implements <see cref="IArchivableEntityAdapter"/>. Shipped by a package that knows no products.</summary>
    [Ability("entity.archive", "Archive entity", Kind = AbilityKind.Write, Reversible = true,
        Description = "Archives an entity, in the active change set.")]
    public sealed class ArchiveEntityAbility : EntityCapabilityBase<IArchivableEntityAdapter, ArchiveInput>
    {
        public override string Capability => "archive";

        protected override object? Apply(Entity entity, IArchivableEntityAdapter adapter, ArchiveInput input, EntityEditContext context)
        {
            EntityEditor.SetValue(Target(entity, input, context), adapter.ArchivedField, true);
            return new { archived = entity.Key };
        }
    }

    // ── A product with a tree of folders ─────────────────────────────────────────────────────

    /// <summary>
    /// Folders in a tree, kept in memory: they can be deleted (not restored), moved and archived. Its authorizer records
    /// every check and refuses what a test asks it to.
    /// </summary>
    private sealed class FolderAdapter : IAuthorizingEntityAdapter, IDeletableEntityAdapter, ITreeEntityAdapter, IArchivableEntityAdapter
    {
        private static readonly ObjectSchema Schema = ObjectSchema.Of(
            new FieldDefinition("name", "The folder's name.", FieldType.String, Required: true),
            new FieldDefinition("parent", "The parent folder; none at the root.", FieldType.EntityReference) { ReferencesEntityType = "folder" },
            new FieldDefinition("hidden", "Hidden from lists.", FieldType.Boolean));

        public Dictionary<Guid, JsonObject> Live { get; } = [];

        public List<Guid> Deleted { get; } = [];

        public List<string> Log { get; } = [];

        public List<EntityAccessRequest> Checked { get; } = [];

        public EntityPermission? Refuse { get; set; }

        public string EntityType => "folder";

        public bool CanCreate => true;

        public bool CanRestore => false;

        public string ParentField => "parent";

        public string ArchivedField => "hidden";

        public IUnitOfWorkProvider UnitOfWork { get; } = new InMemoryDatabase("Folders");

        public Guid Seed(string name, Guid? parent = null)
        {
            var key = Guid.NewGuid();
            Live[key] = new JsonObject { ["name"] = name, ["parent"] = parent?.ToString() };
            return key;
        }

        public Task<IReadOnlyList<EntitySubtype>> GetSubtypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EntitySubtype>>([]);

        public Task<EntitySchema> GetSchemaAsync(string? subtype, CancellationToken cancellationToken = default)
            => Task.FromResult(new EntitySchema(EntityType, "Folder", "A folder.", Schema));

        public Task<Entity> NewAsync(Guid key, string? subtype, CancellationToken cancellationToken = default)
            => Task.FromResult(new Entity { EntityType = EntityType, Key = key });

        public Task<Entity?> LoadAsync(Guid key, CancellationToken cancellationToken = default)
        {
            if (!Live.TryGetValue(key, out var fields))
            {
                return Task.FromResult<Entity?>(null);
            }

            var copy = (JsonObject)fields.DeepClone();
            if (copy["parent"] is null)
            {
                copy.Remove("parent");
            }

            return Task.FromResult<Entity?>(new Entity { EntityType = EntityType, Key = key, Version = 1, Fields = copy });
        }

        public Task<IReadOnlyList<Problem>> ValidateAsync(Entity entity, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Problem>>([]);

        public Task<EntitySummary> SaveAsync(Entity entity, CancellationToken cancellationToken = default)
        {
            Log.Add($"save {entity.Fields["name"]}");
            Live[entity.Key] = (JsonObject)entity.Fields.DeepClone();
            return Task.FromResult(ToSummary(entity));
        }

        public Task DeleteAsync(Guid key, CancellationToken cancellationToken = default)
        {
            Log.Add($"delete {Live[key]["name"]}");
            Live.Remove(key);
            Deleted.Add(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EntitySummary>> FindAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EntitySummary>>(Live
                .Where(f => f.Value["name"]!.GetValue<string>().Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(f => new EntitySummary(EntityType, f.Key, f.Value["name"]!.GetValue<string>(), string.Empty))
                .ToList());

        public EntitySummary ToSummary(Entity entity) => new(EntityType, entity.Key, entity.Fields["name"]?.GetValue<string>() ?? "(unnamed)", string.Empty);

        public Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Permission == EntityPermission.Read)
            {
                return Task.FromResult<string?>(null);
            }

            Checked.Add(request);
            return Task.FromResult<string?>(request.Permission == Refuse ? "No." : null);
        }
    }
}
