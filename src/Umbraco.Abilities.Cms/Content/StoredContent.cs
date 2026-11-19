using System.Text.Json.Nodes;

namespace Umbraco.Abilities.Cms;

/// <summary>A content item as stored, in editor (Management API) format: what a content-type adapter loads.</summary>
/// <param name="Key">The item's key.</param>
/// <param name="TypeKey">Its content type's key.</param>
/// <param name="Names">Its name by culture, with <c>""</c> for an invariant name.</param>
/// <param name="Values">Its property values, one per property, culture and segment.</param>
/// <param name="Version">Its version, to tell an existing item from a new one.</param>
/// <param name="ParentKey">
/// Its parent: for a new item not saved yet (a draft), so the parent survives being read back and edited again, and for
/// a saved item whose adapter can move it. Null at the root, and for a saved item that can't be moved.
/// </param>
public sealed record StoredContent(Guid Key, Guid TypeKey, IReadOnlyDictionary<string, string> Names, IReadOnlyList<StoredValue> Values, long? Version, Guid? ParentKey = null);

/// <summary>One stored property value, in editor format.</summary>
public sealed record StoredValue(string Alias, string? Culture, string? Segment, JsonNode? Value);
