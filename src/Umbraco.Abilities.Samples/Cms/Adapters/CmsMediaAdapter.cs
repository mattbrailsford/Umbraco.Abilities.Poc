using System.ComponentModel.DataAnnotations;

using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Entities.Models;

namespace Umbraco.Abilities.Samples.Cms;

/// <summary>
/// Media items, so pickers and references can point at media created in the same change set. The model is described
/// fluently, so the request models stay as they are.
/// </summary>
public sealed class CmsMediaAdapter(InMemoryDatabase database, ItemTypeRegistry itemTypes)
    : InMemoryModelAdapter<MediaRequestModel>(database, itemTypes, "/umbraco/section/media/workspace/media/edit")
{
    protected override void Configure(ModelConfigurator<MediaRequestModel> models) => models
        .Entity(media => media
            .IsEntity("media", "Media", "An image or file.")
            .HasKey(x => x.Id)
            .HasVersion(x => x.Version)
            .Ignore(x => x.UpdateDate)
            .Field(x => x.Name).IsName().Required().Description("Media name.")
            .Field(x => x.AltText).Description("Alternative text.")
            .Items(x => x.Crops).Description("Named crops of the image."))
        .Model<MediaCropRequestModel>(crop => crop
            .IsItemType("crop", "Crop", "A named crop.")
            .HasKey(x => x.Id)
            .Field(x => x.Alias).Required().Description("Crop alias, e.g. thumbnail.")
            .Field(x => x.Width).Required().Rule(new RangeAttribute(1, 10000)).Description("Width in pixels.")
            .Field(x => x.Height).Required().Rule(new RangeAttribute(1, 10000)).Description("Height in pixels."));
}
