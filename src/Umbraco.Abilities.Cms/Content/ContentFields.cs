using Umbraco.Abilities.Entities;

namespace Umbraco.Abilities.Cms;

/// <summary>The fields every content-type entity has besides its properties.</summary>
public static class ContentFields
{
    /// <summary>The name, per culture when the type varies by culture.</summary>
    public const string Name = "name";

    /// <summary>The parent. For an adapter that is an <see cref="ITreeEntityAdapter"/>, changing it moves the item; otherwise it is only used when creating.</summary>
    public const string Parent = "parent";
}
