namespace Umbraco.Abilities.Entities;

/// <summary>
/// The property names an entity has in JSON: in what the entity tools take and return, and in a reference to one. A
/// problem at one of them uses the same name as its path.
/// </summary>
public static class EntityJson
{
    /// <summary>The entity type, such as <c>document</c>.</summary>
    public const string EntityType = "entityType";

    /// <summary>The entity's key.</summary>
    public const string Key = "key";

    /// <summary>The subtype, such as a page type's alias.</summary>
    public const string Subtype = "subtype";

    /// <summary>The version the entity was read at.</summary>
    public const string Version = "version";

    /// <summary>The entity's values, by field alias.</summary>
    public const string Fields = "fields";
}

/// <summary>
/// The schema keywords entity schemas add, beyond JSON Schema's own. Every reader names them from here. The ones meant
/// for forms and for one audience are in <see cref="UiHints"/>.
/// </summary>
public static class SchemaKeywords
{
    /// <summary>How to edit a field, for a model: the path form and the tools to use.</summary>
    public const string Edit = "x-edit";

    /// <summary>A field that takes a value bound from an earlier step.</summary>
    public const string Bindings = "x-bindings";

    /// <summary>The dimensions a field varies by, such as culture.</summary>
    public const string Variations = "x-variations";

    /// <summary>The item types a list of items can hold.</summary>
    public const string ItemTypes = "x-item-types";

    /// <summary>The entity type, or types, a key points at.</summary>
    public const string References = "x-references";

    /// <summary>The field whose items a key points at.</summary>
    public const string ReferencesItemIn = "x-references-item-in";
}
