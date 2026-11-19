namespace Umbraco.Abilities.Samples.Cms;

/// <summary>Stands in for a Management API request model in another assembly: a package can't put attributes on it.</summary>
public sealed class MediaRequestModel
{
    public Guid Id { get; set; }

    public long? Version { get; set; }

    public string? Name { get; set; }

    public string? AltText { get; set; }

    public List<MediaCropRequestModel> Crops { get; set; } = [];

    /// <summary>Server-side state the editing contract leaves out.</summary>
    public DateTime? UpdateDate { get; set; }
}

public sealed class MediaCropRequestModel
{
    public Guid Id { get; set; }

    public string? Alias { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }
}
