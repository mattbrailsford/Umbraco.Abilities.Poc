using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Entities;

/// <summary>
/// One plugin as its product's registry describes it: a Commerce discount rule provider, an Automate action. A product
/// maps its own plugin metadata to this once per seam; <see cref="PluginItemTypeSource"/> turns every plugin into an
/// item type, and <see cref="PluginSettings"/> maps its items to and from the product's stored settings.
/// </summary>
public sealed record PluginDescriptor(string Alias, string? Name, string? Description, IReadOnlyList<SettingDescriptor> Settings)
{
    /// <summary>Fields the product adds beside the plugin's own settings, such as a group rule's children or a step's exits.</summary>
    public IReadOnlyList<FieldDefinition> ExtraFields { get; init; } = [];

    /// <summary>How the product stores the plugin's settings as text. Default: .NET's own form.</summary>
    public IPluginSettingFormat SettingFormat { get; init; } = DotNetPluginSettingFormat.Instance;

    /// <summary>Whether a user may use the plugin, e.g. only users with its required sections. Null: everyone.</summary>
    public Func<Guid, CancellationToken, ValueTask<bool>>? AvailableTo { get; init; }

    /// <summary>The CLR type a setting holds, or null for a setting the plugin doesn't declare.</summary>
    public Type? SettingType(string key) => Settings.FirstOrDefault(s => s.Key == key)?.ClrType;
}

/// <summary>
/// The item types of a plugin seam, read from the product's registry each time they're needed. This is all a product
/// writes per seam: it says where its plugins are and how to describe one. Plugins, third parties' included, write nothing.
/// </summary>
public sealed class PluginItemTypeSource : IItemTypeSource
{
    private static readonly IReadOnlyDictionary<string, string[]> NoEditors = new Dictionary<string, string[]>();

    private readonly string _group;
    private readonly Func<CancellationToken, Task<IEnumerable<PluginDescriptor>>> _plugins;
    private readonly IReadOnlyDictionary<string, string[]> _editorReferences;

    /// <param name="group">The item type group the seam's items field names.</param>
    /// <param name="plugins">The plugins registered now. Read once per call, so later plugins show up.</param>
    /// <param name="editorReferences">The product's table from an editor UI alias to the entity types it picks.</param>
    public PluginItemTypeSource(string group, Func<CancellationToken, Task<IEnumerable<PluginDescriptor>>> plugins, IReadOnlyDictionary<string, string[]>? editorReferences = null)
        => (_group, _plugins, _editorReferences) = (group, plugins, editorReferences ?? NoEditors);

    /// <summary>For plugins held in memory, such as a DI collection.</summary>
    public PluginItemTypeSource(string group, Func<IEnumerable<PluginDescriptor>> plugins, IReadOnlyDictionary<string, string[]>? editorReferences = null)
        : this(group, _ => Task.FromResult(plugins()), editorReferences)
    {
    }

    public string Group => _group;

    public bool Serves(string group) => group == _group;

    public async Task<IEnumerable<ItemTypeDefinition>> GetItemTypesAsync(string group, CancellationToken cancellationToken = default)
        => (await _plugins(cancellationToken)).Select(Describe).ToList();

    private ItemTypeDefinition Describe(PluginDescriptor plugin)
    {
        var fields = plugin.Settings.OrderBy(s => s.SortOrder).Select(s => ItemTypeFields.For(s, _editorReferences)).ToList();
        if (plugin.ExtraFields.FirstOrDefault(e => fields.Any(f => f.Alias == e.Alias)) is { } clash)
        {
            throw new InvalidOperationException($"{plugin.Alias} has a setting called '{clash.Alias}', which is a field its product adds.");
        }

        fields.AddRange(plugin.ExtraFields);
        return new ItemTypeDefinition(
            _group,
            plugin.Alias,
            ItemTypeFields.Text(plugin.Name) ?? plugin.Alias,
            ItemTypeFields.Text(plugin.Description)
                ?? $"Its product gives this plugin no description, or only a localization key. Describe it in plain words from its alias, '{plugin.Alias}'.",
            new ObjectSchema(fields))
        {
            AvailableTo = plugin.AvailableTo,
        };
    }
}

/// <summary>Item types worked out once per call, such as one per ability that can be a workflow step.</summary>
public sealed class ItemTypeSource : IItemTypeSource
{
    private readonly Func<CancellationToken, Task<IEnumerable<ItemTypeDefinition>>> _itemTypes;

    public ItemTypeSource(string group, Func<CancellationToken, Task<IEnumerable<ItemTypeDefinition>>> itemTypes)
        => (Group, _itemTypes) = (group, itemTypes);

    /// <summary>For item types worked out from what's in memory.</summary>
    public ItemTypeSource(string group, Func<IEnumerable<ItemTypeDefinition>> itemTypes)
        : this(group, _ => Task.FromResult(itemTypes()))
    {
    }

    public string Group { get; }

    public bool Serves(string group) => group == Group;

    public Task<IEnumerable<ItemTypeDefinition>> GetItemTypesAsync(string group, CancellationToken cancellationToken = default) => _itemTypes(cancellationToken);
}

/// <summary>
/// How a product stores one plugin setting as text. The framework turns a model's JSON into the setting's CLR type
/// (an enum, a number, a dictionary of amounts) and back; the format turns that typed value into the product's stored
/// text and back. A product whose editors expect something else (its own serializer, its own enum names) supplies its
/// own format on <see cref="PluginDescriptor.SettingFormat"/>.
/// </summary>
public interface IPluginSettingFormat
{
    /// <summary>The stored text for a typed value. <paramref name="type"/> is the setting's type, never nullable.</summary>
    string Write(object value, Type type);

    /// <summary>The typed value for stored text. Throws when the text isn't in the form the type expects.</summary>
    object? Read(string text, Type type);
}

/// <summary>
/// .NET's own form, which Umbraco Commerce's setting editors use: an enum is its member name ("TotalPrice"), a number
/// is invariant text, a bool is "true" or "false", and an object or list is plain JSON of the typed value. Reading
/// accepts numbers as text and enum names in any case, as settings written by hand or by older code may hold.
/// </summary>
public sealed class DotNetPluginSettingFormat : IPluginSettingFormat
{
    /// <summary>The one instance: it holds no state.</summary>
    public static readonly DotNetPluginSettingFormat Instance = new();

    private static readonly JsonSerializerOptions Writing = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly JsonSerializerOptions Reading = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    private DotNetPluginSettingFormat()
    {
    }

    public string Write(object value, Type type) => value switch
    {
        Enum member => member.ToString(),
        bool flag => flag ? "true" : "false",
        IFormattable number when type.IsPrimitive || type == typeof(decimal) => number.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, type, Writing),
    };

    public object? Read(string text, Type type)
        => type.IsEnum ? Enum.Parse(type, text, ignoreCase: true) : JsonSerializer.Deserialize(text, type, Reading);
}

/// <summary>
/// Maps a plugin's item to and from the way most products store plugin settings: the plugin's alias plus a string per
/// setting. A model sees each value as its setting's CLR type in the framework's JSON (camel-case enum names, numbers
/// as numbers), whatever form the product stores it in: that is the plugin's <see cref="PluginDescriptor.SettingFormat"/>.
/// </summary>
public static class PluginSettings
{
    /// <summary>An item from a plugin's stored settings.</summary>
    public static DynamicItem ToItem(Guid key, string alias, IReadOnlyDictionary<string, string> settings, PluginDescriptor? plugin)
    {
        var item = new DynamicItem { Key = key, Type = alias };
        foreach (var (settingKey, text) in settings)
        {
            item.Fields[settingKey] = ToField(plugin, settingKey, text);
        }

        return item;
    }

    /// <summary>
    /// An item's values as stored settings, as text, leaving out the fields named (a group's children, a step's exits).
    /// Without the plugin, each value is stored as the model gave it.
    /// </summary>
    public static Dictionary<string, string> ToSettings(DynamicItem item, params string[] except)
        => ToSettings(item, null, except);

    /// <summary>
    /// An item's values as stored settings, each in its plugin's <see cref="PluginDescriptor.SettingFormat"/> for the
    /// type the plugin declares for it, leaving out the fields named (a group's children, a step's exits).
    /// </summary>
    public static Dictionary<string, string> ToSettings(DynamicItem item, PluginDescriptor? plugin, params string[] except)
        => item.Fields
            .Where(f => f.Value is not null && !except.Contains(f.Key))
            .ToDictionary(f => f.Key, f => ToStored(plugin, f.Key, f.Value!), StringComparer.Ordinal);

    // Stored text to the value a model sees: the JSON the schema describes.
    private static JsonNode? ToField(PluginDescriptor? plugin, string key, string text)
    {
        if (plugin is null || TypeOf(plugin, key) is not { } type)
        {
            return JsonValue.Create(text);
        }

        try
        {
            return JsonSerializer.SerializeToNode(plugin.SettingFormat.Read(text, type), type, AbilityJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or NotSupportedException)
        {
            // Not in the form the type expects: show it as it was stored.
            return JsonValue.Create(text);
        }
    }

    // A model's value to stored text: through the setting's type, then the product's format.
    private static string ToStored(PluginDescriptor? plugin, string key, JsonNode value)
    {
        if (plugin is not null && TypeOf(plugin, key) is { } type)
        {
            try
            {
                if (value.Deserialize(type, AbilityJson.Options) is { } typed)
                {
                    return plugin.SettingFormat.Write(typed, type);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Not in the form the type expects; validation reports it. Store it as given.
            }
        }

        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }

    // The setting's type without Nullable, or null for a string or an undeclared setting: those are stored as given.
    private static Type? TypeOf(PluginDescriptor plugin, string key)
        => plugin.SettingType(key) is { } type && (Nullable.GetUnderlyingType(type) ?? type) is var target && target != typeof(string) ? target : null;
}
