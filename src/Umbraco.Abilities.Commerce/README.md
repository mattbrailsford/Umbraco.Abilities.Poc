# Umbraco.Abilities.Commerce

Real Umbraco Commerce, as entity types: discounts to create and edit, and stores and currencies to point at. This is
what Commerce would ship. It adds no abilities of its own: the generic entity tools work on discounts as they are.

## Read it in this order

1. `Models/DiscountModel.cs`: the editing contract. What a model and a person see.
2. `Adapters/CommerceDiscountAdapter.cs`: load, save, validate, find and who may edit. One method each.
3. `Adapters/CommerceDiscountAdapter.Mapping.cs`: how the contract maps to Commerce's own discount, and how its rule
   and reward providers become item types.
4. `CommerceAbilitiesBuilderExtensions.cs`: the one call a site makes.

## Folders

| Folder | What's in it |
| --- | --- |
| `Models/` | The editing contracts: a discount, and read-only stores and currencies |
| `Adapters/` | One adapter per entity type, mapping Commerce's own model to and from its contract |
| `Infrastructure/` | Commerce's transaction for a change set, and who may use a store |
