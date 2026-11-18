using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Umbraco.Abilities;

/// <summary>
/// The editor a UI should use for a field: a backoffice editor UI alias, such as <c>Umb.PropertyEditorUi.Slider</c>, and
/// its configuration. Written to the schema as <c>x-editor</c>. For people, not models: the AI tools and MCP strip it.
/// </summary>
public sealed record FieldEditor(string Alias, JsonObject? Config = null)
{
    /// <summary>The editor as the <c>x-editor</c> keyword's value.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["alias"] = Alias };
        if (Config is not null)
        {
            json["config"] = Config.DeepClone();
        }

        return json;
    }
}

/// <summary>
/// Names the editor a UI should use for this field or input property, instead of one picked from its type. For one-off
/// cases: a value type that always wants the same editor should name its <c>format</c> instead, and the UI maps that.
/// A model never sees it.
/// </summary>
/// <param name="alias">The editor UI alias, as registered in the backoffice.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class EditorAttribute(string alias) : Attribute
{
    public string Alias { get; } = alias;

    /// <summary>The editor's configuration, as a JSON object, e.g. <c>{"min": 0, "max": 10}</c>.</summary>
    public string? Config { get; init; }

    public FieldEditor ToEditor()
        => new(Alias, Config is null ? null : JsonNode.Parse(Config) as JsonObject
            ?? throw new InvalidOperationException($"The config of editor '{Alias}' must be a JSON object."));
}

/// <summary>
/// Adds a key to the field's <c>x-meta</c> bag: a hint that one product reads and the framework only carries. See
/// <see cref="UiHints"/> for the rules a key follows. A model never sees it.
/// </summary>
/// <param name="key">The key, as <c>owner.name</c>, e.g. <c>acme.color</c>.</param>
/// <param name="json">The value, a JSON object.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class MetaAttribute(string key, string json) : Attribute
{
    public string Key { get; } = key;

    public string Json { get; } = json;
}

/// <summary>
/// The two schema keywords for UIs, not models: <c>x-editor</c> (which editor a field uses) and <c>x-meta</c> (an open
/// bag of hints, each owned by whoever defines it). The framework carries both and acts on neither.
/// <para>
/// Rules for <c>x-meta</c>: keys are <c>owner.name</c>, lowercase, with <c>umbraco.</c> for Umbraco's own packages.
/// Values are JSON objects of at most <see cref="MaxMetaBytes"/> bytes. Hints only: deleting the bag must break nothing.
/// No code, secrets or personal data. Readers ignore keys they don't know and treat values as untrusted. A breaking
/// change is a new key name. A key two products read is proposed as a keyword.
/// </para>
/// </summary>
public static partial class UiHints
{
    /// <summary>The keyword naming a field's editor.</summary>
    public const string EditorKeyword = "x-editor";

    /// <summary>The keyword holding a field's metadata bag.</summary>
    public const string MetaKeyword = "x-meta";

    /// <summary>The largest a metadata value may be, as UTF-8 JSON.</summary>
    public const int MaxMetaBytes = 4096;

    /// <summary>The meta-schema's pattern for a key: an owner, then one or more names, each lowercase.</summary>
    public const string MetaKeyPattern = "^[a-z0-9]+(\\.[a-z0-9-]+)+$";

    [GeneratedRegex(MetaKeyPattern)]
    private static partial Regex MetaKey();

    /// <summary>
    /// Adds a key to a bag, after checking it follows the rules. Returns a new bag; the one given is not changed.
    /// Throws when the key or value breaks a rule, since that is a mistake in the code that sets it.
    /// </summary>
    public static JsonObject WithMeta(JsonObject? bag, string key, JsonNode? value)
    {
        if (!MetaKey().IsMatch(key))
        {
            throw new ArgumentException($"Metadata key '{key}' must be 'owner.name', lowercase, such as 'acme.color'.", nameof(key));
        }

        if (value is not JsonObject)
        {
            throw new ArgumentException($"Metadata '{key}' must be a JSON object.", nameof(value));
        }

        if (Encoding.UTF8.GetByteCount(value.ToJsonString()) > MaxMetaBytes)
        {
            throw new ArgumentException($"Metadata '{key}' is over {MaxMetaBytes} bytes. Keep hints small: the schema is read on every call.", nameof(value));
        }

        var copy = bag?.DeepClone().AsObject() ?? new JsonObject();
        copy[key] = value.DeepClone();
        return copy;
    }

    /// <summary>The editor named by an <see cref="EditorAttribute"/> on any of the given members, if one is.</summary>
    public static FieldEditor? EditorOf(params ICustomAttributeProvider?[] providers)
        => providers
            .Select(p => p?.GetCustomAttributes(typeof(EditorAttribute), inherit: true).OfType<EditorAttribute>().FirstOrDefault())
            .FirstOrDefault(a => a is not null)?.ToEditor();

    /// <summary>The bag built from the <see cref="MetaAttribute"/>s on the given members. Null when there are none.</summary>
    public static JsonObject? MetaOf(params ICustomAttributeProvider?[] providers)
    {
        JsonObject? bag = null;
        foreach (var attribute in providers.SelectMany(p => p?.GetCustomAttributes(typeof(MetaAttribute), inherit: true).OfType<MetaAttribute>() ?? []))
        {
            bag = WithMeta(bag, attribute.Key, JsonNode.Parse(attribute.Json));
        }

        return bag;
    }

    /// <summary>Writes the editor and bag into a field's JSON Schema, when there are any.</summary>
    public static void WriteTo(JsonObject schema, FieldEditor? editor, JsonObject? meta)
    {
        if (editor is not null)
        {
            schema[EditorKeyword] = editor.ToJson();
        }

        if (meta is { Count: > 0 })
        {
            schema[MetaKeyword] = meta.DeepClone();
        }
    }

    /// <summary>The keywords meant for forms and UIs only. A model doesn't read them: the <c>strip-form-keywords</c> filter removes them.</summary>
    public static IReadOnlyList<string> FormKeywords { get; } = [EditorKeyword, MetaKeyword];

    /// <summary>
    /// A copy of a schema or result without the given keywords, at any depth. A JSON value that holds none of them is
    /// returned as is.
    /// </summary>
    public static JsonElement Without(JsonElement json, IReadOnlyCollection<string> keywords)
    {
        var text = json.GetRawText();
        if (!keywords.Any(keyword => text.Contains($"\"{keyword}\"", StringComparison.Ordinal)))
        {
            return json;
        }

        var node = JsonNode.Parse(text);
        Strip(node, keywords);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Strip(JsonNode? node, IReadOnlyCollection<string> keywords)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var keyword in keywords)
                {
                    json.Remove(keyword);
                }

                foreach (var (_, child) in json)
                {
                    Strip(child, keywords);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Strip(child, keywords);
                }

                break;
        }
    }
}
