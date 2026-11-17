using System.Text.Json.Nodes;

namespace Umbraco.Abilities.Entities;

public enum EntityPermission
{
    Read,
    Create,
    Update,

    /// <summary>Delete the entity (<see cref="IDeletableEntityAdapter"/>).</summary>
    Delete,

    /// <summary>Move the entity to another parent (<see cref="ITreeEntityAdapter"/>). The new parent is in the request's entity.</summary>
    Move,
}

/// <summary>
/// Whether a user may read, create or change an entity. <paramref name="Entity"/> is the entity as it would be after
/// the change (so a create can check its parent, and a move its new parent), or null for a read by key; for a delete,
/// the entity as it was. <paramref name="Variants"/> are the variants the change touches, for products that limit users
/// to some of them (the CMS's languages).
/// </summary>
public sealed record EntityAccessRequest(Guid UserKey, EntityPermission Permission, Guid Key, Entity? Entity, IReadOnlySet<VariantId> Variants);

/// <summary>
/// An adapter whose product has its own permission model: CMS user groups, document permissions and language access, or
/// Commerce's store access. Abilities never build their own: they ask the product. Every adapter must implement it:
/// <see cref="EntityAdapterRegistry.Add"/> refuses one that doesn't, as Abilities refuses an ability with no access rule.
/// </summary>
public interface IAuthorizingEntityAdapter : IEntityAdapter
{
    /// <summary>Null when allowed; otherwise why not, in words the user (or an agent acting for them) can be told.</summary>
    Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken cancellationToken = default);
}

public static class EntityAccess
{
    public static async Task<string?> CheckAsync(IEntityAdapter adapter, EntityAccessRequest request, CancellationToken cancellationToken = default)
        => adapter is IAuthorizingEntityAdapter authorizing ? await authorizing.AuthorizeAsync(request, cancellationToken) : null;

    /// <summary>
    /// The checks a change to an entity needs, from what changed: a delete when <paramref name="after"/> is null, a create
    /// for an entity not saved yet; otherwise a move when a tree adapter's parent changed, and an update for everything
    /// else. So a pure move is not also an update, and setting the parent with entity.setValue is checked as a move too.
    /// </summary>
    public static IReadOnlyList<EntityAccessRequest> RequestsFor(Guid userKey, IEntityAdapter adapter, Guid key, Entity? before, Entity? after, ObjectSchema schema, ItemTypes itemTypes)
    {
        if (after is null)
        {
            // Deleting an entity the set created deletes nothing live, so it needs nothing.
            return before?.Version is null ? [] : [new EntityAccessRequest(userKey, EntityPermission.Delete, key, before, new HashSet<VariantId>())];
        }

        if (after.Version is null)
        {
            return [new EntityAccessRequest(userKey, EntityPermission.Create, key, after, VariantsChanged(before, after, schema, itemTypes))];
        }

        var changes = EntityDiff.Compare(before?.Fields ?? [], after.Fields, schema, itemTypes);
        var requests = new List<EntityAccessRequest>();
        var special = new HashSet<string>();
        if (adapter is ITreeEntityAdapter tree)
        {
            special.Add(tree.ParentField);
            if (changes.Any(c => FieldOf(c) == tree.ParentField))
            {
                requests.Add(new EntityAccessRequest(userKey, EntityPermission.Move, key, after, new HashSet<VariantId>()));
            }
        }

        var rest = changes.Where(c => !special.Contains(FieldOf(c))).ToList();
        if (rest.Count > 0 || requests.Count == 0)
        {
            requests.Add(new EntityAccessRequest(userKey, EntityPermission.Update, key, after, rest.Select(c => c.Variant).Where(v => !v.IsInvariant).ToHashSet()));
        }

        return requests;
    }

    /// <summary>The first refusal among <paramref name="requests"/>, or null when all are allowed.</summary>
    public static async Task<string?> CheckAllAsync(IEntityAdapter adapter, IEnumerable<EntityAccessRequest> requests, CancellationToken cancellationToken = default)
    {
        foreach (var request in requests)
        {
            if (await CheckAsync(adapter, request, cancellationToken) is { } denied)
            {
                return denied;
            }
        }

        return null;
    }

    private static string FieldOf(ChangedPath change) => change.Path.Split('/')[0];

    /// <summary>The variants whose values differ between two versions of an entity.</summary>
    public static IReadOnlySet<VariantId> VariantsChanged(Entity? before, Entity after, ObjectSchema schema, ItemTypes itemTypes)
        => EntityDiff.Compare(before?.Fields ?? [], after.Fields, schema, itemTypes)
            .Select(c => c.Variant)
            .Where(v => !v.IsInvariant)
            .ToHashSet();

    /// <summary>
    /// Item types the change adds that the user may not use, such as an Automate action that needs a section they don't
    /// have. Items the entity already had are left alone. Each item's type is looked up in its own field's group, since
    /// the same alias can be a different type in another group. The item types say who may use what: resolve them for the
    /// user making the change.
    /// </summary>
    public static IReadOnlyList<string> NewItemTypesNotAvailable(Entity? before, Entity after, ObjectSchema schema, ItemTypes itemTypes)
    {
        var existing = before is null ? [] : Items(before.Fields, schema, itemTypes).Select(i => i.Key).ToHashSet();
        return Items(after.Fields, schema, itemTypes)
            .Where(i => !existing.Contains(i.Key) && i.Type is { } type && !itemTypes.IsAvailable(type))
            .Select(i => i.Type!.Alias)
            .Distinct()
            .ToList();
    }

    // Every item in the entity, at any depth, with its item type.
    private static IEnumerable<(Guid Key, ItemTypeDefinition? Type)> Items(JsonObject root, ObjectSchema schema, ItemTypes itemTypes)
        => EntityValues.Walk(root, schema, itemTypes)
            .Where(v => v.Field.IsItems)
            .SelectMany(v => (v.Value as JsonArray ?? []).OfType<JsonObject>()
                .Select(item => (EntityValues.KeyOf(item), itemTypes.Find(v.Field.ItemTypeGroup!, EntityValues.TypeOf(item)))));

    public static AbilityResult Denied(string reason, string path = "")
        => AbilityResult.Fail(AbilityOperationStatus.AccessDenied, reason, [new Problem(path, reason)]);
}
