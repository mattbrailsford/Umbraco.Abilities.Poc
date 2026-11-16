using System.Text.Json.Nodes;

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

    /// <summary>Whether a user may use the plugin, e.g. only users with its required sections. Null: everyone.</summary>
    public Func<Guid, bool>? AvailableTo { get; init; }

    /// <summary>The CLR type a setting holds, or null for a setting the plugin doesn't declare.</summary>
    public Type? SettingType(string key) => Settings.FirstOrDefault(s => s.Key == key)?.ClrType;
}

/// <summary>
/// The item types of a plugin seam, read from the product's registry each time they're needed. This is all a product
/// writes per seam: it says where its plugins are and how to describe one. Plugins, third parties' included, write nothing.
/// </summary>
/// <param name="group">The item type group the seam's items field names.</param>
/// <param name="plugins">The plugins registered now. Called whenever the item types are needed, so later plugins show up.</param>
/// <param name="editorReferences">The product's table from an editor UI alias to the entity types it picks.</param>
public sealed class PluginItemTypeSource(
    string group,
    Func<IEnumerable<PluginDescriptor>> plugins,
    IReadOnlyDictionary<string, string[]>? editorReferences = null) : IItemTypeSource
{
    private static readonly IReadOnlyDictionary<string, string[]> NoEditors = new Dictionary<string, string[]>();

    public string Group => group;

    public IEnumerable<ItemTypeDefinition> GetItemTypes() => plugins().Select(plugin =>
    {
        var fields = plugin.Settings.OrderBy(s => s.SortOrder).Select(s => ItemTypeFields.For(s, editorReferences ?? NoEditors)).ToList();
        if (plugin.ExtraFields.FirstOrDefault(e => fields.Any(f => f.Alias == e.Alias)) is { } clash)
        {
            throw new InvalidOperationException($"{plugin.Alias} has a setting called '{clash.Alias}', which is a field its product adds.");
        }

        fields.AddRange(plugin.ExtraFields);
        return new ItemTypeDefinition(
            group,
            plugin.Alias,
            ItemTypeFields.Text(plugin.Name) ?? plugin.Alias,
            ItemTypeFields.Text(plugin.Description)
                ?? $"Its product gives this plugin no description, or only a localization key. Describe it in plain words from its alias, '{plugin.Alias}'.",
            new ObjectSchema(fields))
        {
            AvailableTo = plugin.AvailableTo,
        };
    });
}

/// <summary>Item types worked out whenever they're needed, such as one per ability that can be a workflow step.</summary>
public sealed class ItemTypeSource(string group, Func<IEnumerable<ItemTypeDefinition>> itemTypes) : IItemTypeSource
{
    public string Group => group;

    public IEnumerable<ItemTypeDefinition> GetItemTypes() => itemTypes();
}

/// <summary>
/// Maps a plugin's item to and from the way most products store plugin settings: the plugin's alias plus a string per
/// setting. Each value is read back as its setting's CLR type, so a number stays a number for the schema and validation.
/// </summary>
public static class PluginSettings
{
    /// <summary>An item from a plugin's stored settings.</summary>
    public static DynamicItem ToItem(Guid key, string alias, IReadOnlyDictionary<string, string> settings, PluginDescriptor? plugin)
    {
        var item = new DynamicItem { Key = key, Type = alias };
        foreach (var (settingKey, text) in settings)
        {
            var type = plugin?.SettingType(settingKey) ?? typeof(string);
            var target = Nullable.GetUnderlyingType(type) ?? type;
            item.Fields[settingKey] = target.IsPrimitive || target == typeof(decimal) ? JsonNode.Parse(text) : JsonValue.Create(text);
        }

        return item;
    }

    /// <summary>An item's values as stored settings, leaving out the fields named (a group's children, a step's exits).</summary>
    public static Dictionary<string, string> ToSettings(DynamicItem item, params string[] except)
        => item.Fields
            .Where(f => f.Value is not null && !except.Contains(f.Key))
            .ToDictionary(f => f.Key, f => f.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : f.Value!.ToJsonString(), StringComparer.Ordinal);
}
