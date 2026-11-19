using Umbraco.Abilities.Entities;

namespace Umbraco.Abilities.Samples.Commerce;

/// <summary>Collections, stored in memory. Read only: a test seeds them.</summary>
public sealed class CollectionAdapter(InMemoryDatabase database, ItemTypeRegistry itemTypes)
    : InMemoryModelAdapter<Collection>(database, itemTypes, "/umbraco/section/commerce/collection")
{
    public override bool CanCreate => false;

    public Guid Seed(string name)
    {
        var collection = new Collection { Key = Guid.NewGuid(), Name = name };
        SaveModelAsync(collection).GetAwaiter().GetResult();
        return collection.Key;
    }
}
