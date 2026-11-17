using System.Runtime.CompilerServices;
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

    /// <summary>
    /// The JSON options the product stores the plugin's settings with, so they are stored as its own editors read them.
    /// Pass the options the product already serializes with. Default: <see cref="PluginSettings.DefaultJsonOptions"/>.
    /// </summary>
    public JsonSerializerOptions SettingJsonOptions { get; init; } = PluginSettings.DefaultJsonOptions;

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
/// Maps a plugin's item to and from the way most products store plugin settings: the plugin's alias plus a string per
/// setting. A model sees each value as its setting's CLR type in the framework's JSON (camel-case enum names, numbers
/// as numbers). It is stored with the product's own JSON options (<see cref="PluginDescriptor.SettingJsonOptions"/>):
/// a value that serializes to a JSON string is stored as that string ("TotalPrice"), anything else as JSON text.
/// </summary>
public static class PluginSettings
{
    /// <summary>
    /// .NET's defaults, with enums as their member names: "TotalPrice", invariant numbers, plain JSON for an object.
    /// Umbraco Commerce's setting editors read this form.
    /// </summary>
    public static readonly JsonSerializerOptions DefaultJsonOptions = ReadOnly(new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

    // Reading stored settings is forgiving: numbers written as text, by hand or by older code, still read.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Loose = new();

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
    /// An item's values as stored settings, each through the type its plugin declares for it and the plugin's
    /// <see cref="PluginDescriptor.SettingJsonOptions"/>, leaving out the fields named (a group's children, a step's exits).
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

        var options = Loose.GetValue(plugin.SettingJsonOptions, o => ReadOnly(new JsonSerializerOptions(o) { NumberHandling = o.NumberHandling | JsonNumberHandling.AllowReadingFromString }));
        foreach (var json in new[] { text, JsonSerializer.Serialize(text) })
        {
            try
            {
                // As JSON first (a number, an object); then as a JSON string, the way a string-like value is stored.
                return JsonSerializer.SerializeToNode(JsonSerializer.Deserialize(json, type, options), type, AbilityJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
            }
        }

        // Not in the form the type expects: show it as it was stored.
        return JsonValue.Create(text);
    }

    // A model's value to stored text: through the setting's type, then the product's JSON options.
    private static string ToStored(PluginDescriptor? plugin, string key, JsonNode value)
    {
        if (plugin is not null && TypeOf(plugin, key) is { } type)
        {
            try
            {
                if (value.Deserialize(type, AbilityJson.Options) is { } typed)
                {
                    var stored = JsonSerializer.SerializeToNode(typed, type, plugin.SettingJsonOptions);
                    return stored is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : stored!.ToJsonString();
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Not in the form the type expects; validation reports it. Store it as given.
            }
        }

        return value is JsonValue given && given.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }

    // The setting's type without Nullable, or null for a string or an undeclared setting: those are stored as given.
    private static Type? TypeOf(PluginDescriptor plugin, string key)
        => plugin.SettingType(key) is { } type && (Nullable.GetUnderlyingType(type) ?? type) is var target && target != typeof(string) ? target : null;

    private static JsonSerializerOptions ReadOnly(JsonSerializerOptions options)
    {
        options.TypeInfoResolver ??= new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        return options;
    }
}
