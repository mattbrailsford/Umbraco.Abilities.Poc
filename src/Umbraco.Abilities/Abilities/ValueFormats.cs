using System.Reflection;
using System.Text.RegularExpressions;

namespace Umbraco.Abilities;

/// <summary>
/// Names the kind of value a type or property holds, written to the schema as JSON Schema's <c>format</c>, such as
/// <c>email</c> or <c>umbraco.cron</c>. On a type, every field of that type gets it, in any ability or entity, so a UI
/// can map the format to one editor once. Unlike <c>x-editor</c>, a model sees it: it says what the value is.
/// <para>
/// A name is one of JSON Schema's own formats, with its standard meaning, or <c>owner.name</c> in lower case for a
/// format a package defines. A type with a format is written as a string, so it needs a JSON converter that writes one.
/// </para>
/// </summary>
/// <param name="format">The format's name.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ValueFormatAttribute(string format) : Attribute
{
    public string Format { get; } = ValueFormats.Check(format);
}

/// <summary>The rules for value format names, and where a type or member's format comes from.</summary>
public static partial class ValueFormats
{
    /// <summary>JSON Schema 2020-12's own formats. A package uses these with their standard meaning, unprefixed.</summary>
    public static readonly IReadOnlySet<string> Standard = new HashSet<string>(StringComparer.Ordinal)
    {
        "date-time", "date", "time", "duration", "email", "idn-email", "hostname", "idn-hostname", "ipv4", "ipv6",
        "uri", "uri-reference", "iri", "iri-reference", "uuid", "uri-template", "json-pointer", "relative-json-pointer",
        "regex",
    };

    /// <summary>The meta-schema's pattern for a package's own format: an owner, then one or more names, in lower case.</summary>
    public const string OwnedPattern = "^[a-z0-9]+(\\.[a-z0-9-]+)+$";

    [GeneratedRegex(OwnedPattern)]
    private static partial Regex Owned();

    /// <summary>Returns the name if it follows the rules, and throws if not: a bad name is a mistake in the code that sets it.</summary>
    public static string Check(string format)
        => Standard.Contains(format) || Owned().IsMatch(format)
            ? format
            : throw new ArgumentException(
                $"Format '{format}' must be one of JSON Schema's own formats, or 'owner.name' in lower case, such as 'umbraco.cron'.",
                nameof(format));

    /// <summary>The format a type declares, looking through <see cref="Nullable{T}"/>. Null when it declares none.</summary>
    public static string? Of(Type type)
        => (Nullable.GetUnderlyingType(type) ?? type).GetCustomAttribute<ValueFormatAttribute>()?.Format;

    /// <summary>The format named on any of the given members, if one is.</summary>
    public static string? OfMember(params ICustomAttributeProvider?[] providers)
        => providers
            .Select(p => p?.GetCustomAttributes(typeof(ValueFormatAttribute), inherit: true).OfType<ValueFormatAttribute>().FirstOrDefault())
            .FirstOrDefault(a => a is not null)?.Format;
}
