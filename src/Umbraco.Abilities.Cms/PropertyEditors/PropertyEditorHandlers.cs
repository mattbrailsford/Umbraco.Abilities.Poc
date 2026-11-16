using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;

namespace Umbraco.Abilities.Cms.PropertyEditors;

/// <summary>
/// How one property editor's values become entity fields, and back. A property editor's package implements it once, and
/// every property using the editor is described, read and written by it, with no change to the document adapter.
/// <para>
/// A handler owns three things: the field a property becomes (<see cref="DescribeAsync"/>), the editor's stored value
/// as the field's value (<see cref="ReadAsync"/>), and the field's value as the stored value (<see cref="WriteAsync"/>).
/// The adapter owns the rest: the property's alias and variation, cultures and segments, and saving through Umbraco.
/// An editor with no handler is a whole JSON value, described by Umbraco's value schema for the editor.
/// </para>
/// <para>
/// Handlers are found in scanned assemblies on their own (<see cref="PropertyEditorEntityHandlersComposer"/>), so a
/// package only has to ship one. Each handler must write back everything it read: the adapter rewrites a property's
/// whole value when an edit touches it, so anything a handler drops on read is lost on save. Keep product data that
/// isn't a field under a <c>$</c> name on the value, as the block handler keeps a block's settings key.
/// </para>
/// </summary>
public interface IPropertyEditorEntityHandler : IDiscoverable
{
    /// <summary>Whether this handler maps the values of the property editor with this alias.</summary>
    bool Handles(string editorAlias);

    /// <summary>
    /// The field a property using the editor becomes. Start from <see cref="PropertyEditorContext.Field"/> or
    /// <see cref="PropertyEditorContext.Items"/>: the adapter sets the field's alias and variation from the property.
    /// </summary>
    Task<FieldDefinition> DescribeAsync(PropertyEditorContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// A stored value, in the editor (Management API) format, as the field's value. Null leaves the field unset. For
    /// an items field, an array of items (<see cref="EntityValues.NewItem"/>).
    /// </summary>
    Task<JsonNode?> ReadAsync(JsonNode stored, PropertyValueReader reader, CancellationToken cancellationToken = default);

    /// <summary>The field's value as a stored value, in the editor format Umbraco validates and saves.</summary>
    Task<JsonNode?> WriteAsync(JsonNode value, PropertyValueWriter writer, CancellationToken cancellationToken = default);
}

/// <summary>
/// For a handler whose field holds items: serves the item types of the groups its fields name, for example one group
/// per data type, read from its configuration each call so they follow it.
/// </summary>
public interface IPropertyEditorItemTypes
{
    /// <summary>Whether this handler serves the group.</summary>
    bool Serves(string group);

    /// <summary>The group's item types now. <paramref name="elements"/> maps element types to schemas.</summary>
    Task<IEnumerable<ItemTypeDefinition>> GetItemTypesAsync(string group, IElementSchemas elements, CancellationToken cancellationToken = default);
}

/// <summary>Element types as object schemas, with each property mapped by its editor's handler.</summary>
public interface IElementSchemas
{
    /// <summary>The fields of an element type, for an item type or an object field (block settings).</summary>
    Task<ObjectSchema> SchemaOfAsync(Guid elementTypeKey, CancellationToken cancellationToken = default);
}

/// <summary>The property a handler describes, reads or writes, and its data type with the editor's configuration.</summary>
public class PropertyEditorContext(IPropertyType property, IDataType? dataType)
{
    public IPropertyType Property { get; } = property;

    /// <summary>The data type, whose <see cref="IDataType.ConfigurationObject"/> is the editor's configuration.</summary>
    public IDataType? DataType { get; } = dataType;

    /// <summary>The property's description, or its name when it has none.</summary>
    public string Description => string.IsNullOrWhiteSpace(Property.Description) ? Property.Name : Property.Description;

    /// <summary>A setting of the data type's configuration, by alias, as JSON. Null when it isn't set.</summary>
    public JsonNode? Setting(string alias)
        => DataType?.ConfigurationData.TryGetValue(alias, out var value) == true && value is not null
            ? value as JsonNode ?? JsonSerializer.SerializeToNode(value)
            : null;

    /// <summary>A field for the property's value: its alias, description and whether it is mandatory.</summary>
    public FieldDefinition Field(FieldType type) => new(Property.Alias, Description, type, Property.Mandatory);

    /// <summary>An items field for the property, holding the group's item types. Mandatory means at least one.</summary>
    public FieldDefinition Items(string itemTypeGroup)
        => FieldDefinition.ItemsOf(Property.Alias, Description, itemTypeGroup) with { Min = Property.Mandatory ? 1 : null };
}

/// <summary>What a handler gets to read a stored value: the property, its field, and a way to read element values.</summary>
public sealed class PropertyValueReader(PropertyEditorContext context, FieldDefinition field, Func<JsonObject, Guid, IEnumerable<ValueModelBase>, Task> readElement)
    : PropertyEditorContext(context.Property, context.DataType)
{
    /// <summary>The field the property became.</summary>
    public FieldDefinition EntityField { get; } = field;

    /// <summary>
    /// Reads an element's values (a block's, or its settings) into <paramref name="into"/>, each property by its own
    /// editor's handler. For editors whose values hold elements. Any of Umbraco's value models will do, such as a
    /// block's <c>BlockPropertyValue</c>.
    /// </summary>
    public Task ReadElementAsync(JsonObject into, Guid elementTypeKey, IEnumerable<ValueModelBase> values)
        => readElement(into, elementTypeKey, values);
}

/// <summary>An element's values as written, with what the editor needs to store and expose it.</summary>
/// <typeparam name="TValue">The editor's own value model, such as a block's <c>BlockPropertyValue</c>.</typeparam>
/// <param name="Values">The element's property values, in editor format.</param>
/// <param name="Errors">How to locate Umbraco's errors inside the element's own properties.</param>
/// <param name="VariesByCulture">Whether the element type varies by culture.</param>
/// <param name="Cultures">The cultures the element has values in.</param>
public sealed record ElementValues<TValue>(IReadOnlyList<TValue> Values, PropertyErrorLocators Errors, bool VariesByCulture, IReadOnlyList<string> Cultures)
    where TValue : ValueModelBase;

/// <summary>One property value of an element, as the adapter writes it, before it becomes an editor's value model.</summary>
internal sealed record ElementValue(string Alias, string? Culture, string? Segment, object? Value);

/// <summary>An element's values as the adapter writes them.</summary>
internal sealed record WrittenElement(IReadOnlyList<ElementValue> Values, PropertyErrorLocators Errors, bool VariesByCulture, IReadOnlyList<string> Cultures);

/// <summary>What a handler gets to write a value: the property, where the value is, and a way to write element values.</summary>
public sealed class PropertyValueWriter : PropertyEditorContext
{
    private readonly Action<ValueErrorLocator> _locateErrors;
    private readonly Func<JsonObject, Guid, string, string?, Task<WrittenElement>> _writeElement;

    internal PropertyValueWriter(
        PropertyEditorContext context,
        FieldDefinition field,
        string path,
        string? culture,
        IReadOnlyList<string> documentCultures,
        Action<ValueErrorLocator> locateErrors,
        Func<JsonObject, Guid, string, string?, Task<WrittenElement>> writeElement)
        : base(context.Property, context.DataType)
    {
        EntityField = field;
        Path = path;
        Culture = culture;
        DocumentCultures = documentCultures;
        _locateErrors = locateErrors;
        _writeElement = writeElement;
    }

    /// <summary>The field the property became.</summary>
    public FieldDefinition EntityField { get; }

    /// <summary>The entity path of the value, e.g. <c>blocks</c> or <c>blocks/{key}/cards</c>.</summary>
    public string Path { get; }

    /// <summary>The culture the value is in: its own, or the culture of the block value it is inside. Null when invariant.</summary>
    public string? Culture { get; }

    /// <summary>The cultures the document has values in.</summary>
    public IReadOnlyList<string> DocumentCultures { get; }

    /// <summary>
    /// Writes an element's values from an item (or its settings), each property by its own editor's handler, as the
    /// value model the editor stores, such as a block's <c>BlockPropertyValue</c>.
    /// </summary>
    public async Task<ElementValues<TValue>> WriteElementAsync<TValue>(JsonObject from, Guid elementTypeKey, string path)
        where TValue : ValueModelBase, new()
    {
        var written = await _writeElement(from, elementTypeKey, path, Culture);
        var values = written.Values.Select(v => new TValue { Alias = v.Alias, Culture = v.Culture, Segment = v.Segment, Value = v.Value }).ToList();
        return new ElementValues<TValue>(values, written.Errors, written.VariesByCulture, written.Cultures);
    }

    /// <summary>
    /// Tells the adapter how to turn Umbraco's validation errors inside this value (a JSON path into the stored value)
    /// into entity paths. Without it, such an error is reported on the property, with the JSON path.
    /// </summary>
    public void LocateErrorsWith(ValueErrorLocator locator) => _locateErrors(locator);
}

/// <summary>Where an error is in the entity: a path, and the variant it is in.</summary>
public sealed record ErrorLocation(string Path, string? Culture, string? Segment);

/// <summary>Turns a JSON path into a stored value, in a culture and segment, into an entity location, or null if unknown.</summary>
public delegate ErrorLocation? ValueErrorLocator(string jsonPath, string? culture, string? segment);

/// <summary>The error locators of the values written for an object's properties, by alias and variant.</summary>
public sealed class PropertyErrorLocators
{
    private readonly Dictionary<(string Alias, string? Culture, string? Segment), ValueErrorLocator> _locators = [];

    internal void Add(string alias, VariantId variant, ValueErrorLocator locator) => _locators[(alias, variant.Culture, variant.Segment)] = locator;

    /// <summary>Locates an error in a property's value, falling back to its invariant value.</summary>
    public ErrorLocation? Locate(string alias, string? culture, string? segment, string jsonPath)
        => (_locators.GetValueOrDefault((alias, culture, segment)) ?? _locators.GetValueOrDefault((alias, null, null)))?.Invoke(jsonPath, culture, segment);
}

/// <summary>The handlers in the order they're asked, with the whole-JSON fallback for editors none handles.</summary>
internal sealed class PropertyEditorHandlerCollection(IEnumerable<IPropertyEditorEntityHandler> handlers, IPropertyEditorEntityHandler fallback)
{
    private readonly IReadOnlyList<IPropertyEditorEntityHandler> _handlers = handlers.ToList();

    public IPropertyEditorEntityHandler For(string editorAlias) => _handlers.FirstOrDefault(h => h.Handles(editorAlias)) ?? fallback;

    public IEnumerable<IPropertyEditorItemTypes> ItemTypes => _handlers.OfType<IPropertyEditorItemTypes>();
}

/// <summary>The item types of every group a handler serves, read through that handler each call.</summary>
internal sealed class PropertyEditorItemTypeSource(PropertyEditorHandlerCollection handlers, IElementSchemas elements) : IItemTypeSource
{
    public bool Serves(string group) => handlers.ItemTypes.Any(h => h.Serves(group));

    public Task<IEnumerable<ItemTypeDefinition>> GetItemTypesAsync(string group, CancellationToken cancellationToken = default)
        => handlers.ItemTypes.First(h => h.Serves(group)).GetItemTypesAsync(group, elements, cancellationToken);
}
