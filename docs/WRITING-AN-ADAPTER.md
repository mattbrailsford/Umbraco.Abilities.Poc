# Writing an entity adapter

> Part of the [Abilities proof of concept](../README.md). This is the package developer's view: what a package writes
> so its entities can be created and edited by any surface (the backoffice, Umbraco.AI, MCP, Automate). The design
> behind it is in [ARCHITECTURE.md](ARCHITECTURE.md#layer-2-entity-adapters).

## Describing a contract

A package describes an **editing contract**: C# classes that say what an AI, Automate or MCP can set on an entity.
The contract is not the product's domain model, and nothing goes on the domain model. The adapter maps between
the two in load and save. The framework works out everything else from the contract.

```
Commerce's domain (DiscountReadOnly, AsWritable, provider settings)
        ↑  LoadModelAsync / SaveModelAsync: the mapping the package writes
DiscountEditModel (the editing contract)
        ↓  derived by the framework
schema, JSON Schema, paths, validation, edit abilities, change sets, rebasing
```

There are two ways to describe a contract, and fluent configuration wins where both are used. Attributes cover
everything; fluent configuration covers what request models have needed so far (key, version, name, ignore, fields with
descriptions, required and rules, items fields), and gets an option when a model needs one.

**Attributes**, for a class the package writes for the purpose (`Samples/Commerce/Commerce.cs`):

```csharp
[Entity("umbraco-commerce-discount", "Discount")]
public sealed class DiscountEditModel
{
    [Key] public Guid Key { get; set; }
    [EntityVersion] public long? Version { get; set; }
    [EntityName, Required] public string? Name { get; set; }
    public DateOnly? EndDate { get; set; }
    [EntityKey("document")] public Guid? LandingPage { get; set; }
    [Items(RuleGroup, Min = 1)] public List<DynamicItem> Rules { get; set; } = [];  // a plugin seam: see below
}

[ItemType("hero", "Hero")]          // an item type the package owns can be a class too (Samples/Cms/CmsPage.cs)
public sealed class Hero : PageBlock
{
    [Required] public Varying<string> Heading { get; set; } = [];
}
```

**Fluent configuration**, for a class the package doesn't own or won't annotate, such as a Management API request
model in another assembly. It needs no base class either: items name their own key property
(`Samples/Cms/CmsPage.cs`).

```csharp
ModelConfiguration.Configure<MediaRequestModel>(media => media
    .IsEntity("media", "Media")
    .HasKey(x => x.Id)
    .HasVersion(x => x.Version)
    .Ignore(x => x.UpdateDate)
    .Field(x => x.Name).IsName().Required()
    .Items(x => x.Crops, CropGroup));

ModelConfiguration.Configure<MediaCropRequestModel>(crop => crop
    .IsItemType("crop", "Crop")
    .HasKey(x => x.Id)
    .Field(x => x.Width).Required().Rule(new RangeAttribute(1, 10000)));
```

**The adapter** maps to the domain and adds the rules the contract can't express:

```csharp
public sealed class DiscountAdapter(DiscountService discounts, ...) : EntityAdapterBase<DiscountEditModel>(...)
{
    public override Task<DiscountEditModel?> LoadModelAsync(Guid key, CancellationToken ct) => /* domain → contract */;
    public override Task SaveModelAsync(DiscountEditModel model, CancellationToken ct)
    {
        var discount = discounts.GetDiscount(model.Key)?.AsWritable() ?? discounts.CreateDiscount(model.Key, model.Name!);
        discount.SetName(model.Name!).SetDates(...).SetRules(model.Rules.Select(ToRuleConfig));
        discounts.SaveDiscount(discount);
        return Task.CompletedTask;
    }
    protected override Task<IReadOnlyList<Problem>> ValidateModelAsync(DiscountEditModel d, CancellationToken ct)
        => /* ProblemAt(x => x.EndDate, "Must be on or after startDate.") */;
}
```

What the package no longer writes:

| Was hand-written | Now comes from |
| --- | --- |
| The schema: fields, types, required, allowed values, references, items fields and their limits | Property types, plus attributes or fluent configuration |
| JSON Schema for structured values (a focal point) | `JsonSchemaExporter` over the property type |
| Culture variation | `Varying<T>` (a value per culture), on value and items fields |
| Block settings and a grid block's layout | Ordinary object properties (`Settings`, `Layout`): a class becomes an object field |
| Turning the contract into the working copy and back | Done both ways, keeping item keys and giving each item its path |
| Range and format checks reported on entity paths (`rewards/{key}/percent`) | DataAnnotations, as attributes or fluent `Rule(...)`, on items and objects at any depth |
| A path for a domain problem | `ProblemAt(x => x.EndDate, ...)`, or `ProblemAt(item, r => r.Percent, ...)` for an item |
| An ability that edits an entity, both directly and inside a change set | `ModelEditAbilityBase<TInput, TModel>`: one `Edit(model, input)` method |

`[Required]` and `[AllowedValues]` become part of the schema, so the generic validator checks them per culture.
Every other rule runs on the contract. Each problem is reported once. A renamed property is a compile error, not a
wrong string key at run time.

## What the mapping costs

The Commerce sample now has a domain shaped like the real one (`Samples/Commerce/Domain.cs`). Its read-only entity
has internal constructors and is changed through `AsWritable()`. Dates are UTC `DateTime`s, and rules and rewards
are a provider alias plus string settings, with no ids.

| Part | Statement lines |
| --- | --- |
| Editing contract | 11 |
| Mapping for the discount: load, save, dates, keys | about 35 |
| Domain rules (dates in order, unique code) and item registration | about 18 |
| Item type source: Commerce's providers as item types. Once per seam, for every provider, third parties' included | 17 |
| Provider mapper: an item ↔ provider alias plus string settings, with children for groups. Once for every provider | 20 |

The mapping is ordinary typed C#, roughly the size of a Management API mapper. It is the only per-entity code the
package writes besides the contract.

**One limit the mapping exposed:** Commerce's rules have no ids, so the adapter derives each item's key from the
discount, the position and the provider alias. A rule's key is stable across loads. But a rule the agent added gets
a new key once saved, and moving a rule changes its key, so rebasing sees a move as a remove and an add. Stable ids
on Commerce's rule nodes would fix this. It is a change to suggest to the Commerce team, not something a mapping can
paper over.

## Entities whose schema is data

A CMS document type is defined in the backoffice. A workflow's step types are other packages' abilities. Neither has
a class to describe, so `CmsDocumentAdapter` and `WorkflowAdapter` implement `IEntityAdapter` over the typed
`Entity`. There is still no `JsonObject` digging: they use `item.Settings` or `item.Layout`, not
`item["settings"]["fields"]`. The CMS adapter builds Umbraco's own `BlockListValue`, `BlockGridValue`,
`BlockItemData`, `BlockPropertyValue`, `BlockGridLayoutItem` and `BlockItemVariation` instead of hand-built
envelopes, and Umbraco's serializer reads and writes them. Moving to the typed document took it from 621 lines to 599.

## Values, paths and handlers

An entity is **fields with values**, as content is (a property has a value, and its editor defines what's inside it)
and as JSON Schema describes data. The working copy is one JSON tree (`Entity.Fields`):

```jsonc
{
  "title": { "en-US": "Summer", "da-DK": "Sommer" },      // varies: one value per culture, where the field is
  "tags": ["sale", "summer"],                              // invariant: one value
  "blocks": [                                              // items: keyed, typed objects
    { "key": "…", "type": "hero", "heading": { "en-US": "Summer sale" }, "settings": { "theme": "dark" } }
  ],
  "intro": { "markup": "<p>…</p><umb-block data-key=\"…\"></umb-block>", "blocks": [ { "key": "…", "type": "callout", "text": "…" } ] }
}
```

**The path rule.** A path goes down through values: in an object a step is a field alias (`blocks/{key}/settings/theme`),
in an items field a step is an item's key (`blocks/{key}/heading`). Any other array is set whole. Paths never use
positions, which would point at something else once an item is added above. The schema says which fields vary,
hold items or hold objects, so nothing is guessed from the data. One walker (`EntityValues.Find`) serves editing, diff
and rebasing; another (`EntityValues.Walk`) lists every value for validation, authorization and diff.

| Value | In the schema | Edited with |
| --- | --- | --- |
| A scalar or a reference | `type`, `format`, `enum`, `x-references` | `entity.setValue` at its path |
| An object with described fields (a block's settings, a grid block's layout, Rich Text) | `type: object` with its `properties` | `entity.setValue` at a path inside it, or whole |
| Items | `type: array`, `x-item-types` | `entity.addItem`, `removeItem`, `moveItem` at the items field's path |
| JSON with only a schema (a CMS editor's value) | The editor's own value schema | `entity.setValue`, whole |

**Handlers add behaviour, not schema.** A handler (`IValueHandler`) is only for what a schema can't say. Rich Text
is an object of `markup` and `blocks`, and paths reach both like any other value. Its handler keeps them in step:
adding a block places its tag in the markup, removing or moving one removes or moves its tag, and every tag must have
a block. A typed model declares one with `[ValueHandler(typeof(...))]` on an object property.

Because paths reach inside every described value, rebasing works per value and per item everywhere, Rich Text's blocks
included, and references inside objects and items are checked. Only a JSON value with just a schema is still opaque:
it rebases as one value, and references in it aren't seen.

**Not done yet.** The CMS adapter still maps Block List and Block Grid itself, to the built-in items. The next step
is to make those editors' handlers the ones Umbraco.AI already has (`IAIPropertyValueHandler`), so Abilities and
Umbraco.AI's tools share one handler model and the CMS adapter loses its block code.

## Item types for plugin seams

Discount rules and rewards, and Automate triggers and actions, are plugin seams. Products and third parties register
them, and no one controls them all. So nobody writes an item type per plugin. Each product describes its **registry**
once, with an `IItemTypeSource` per seam, and the item types follow from whatever is registered when they're needed. A
plugin installed later just shows up, and plugins write no Abilities code.

Both registries already carry what an item type needs. The PoC's stand-ins are shaped like the real ones
(Umbraco.Commerce.Core's providers, Umbraco.Commerce.Automate's triggers and actions):

| Item type needs | Commerce rule and reward providers (`Samples/Commerce/Providers.cs`) | Automate triggers and actions (`Samples/Automate/Plugins.cs`) |
| --- | --- | --- |
| Alias, name, description | `[DiscountRuleProvider(alias)]`, with Label and Description | `[Trigger]` / `[Action]`, with name and Description |
| Fields and their types | Setting definitions discovered from the provider's settings class, keeping each CLR type | The settings class's `[Field]` properties |
| Required and other rules | The setting's DataAnnotations (`ValidationRules`) | The setting's DataAnnotations |
| What a field picks | The setting's editor UI alias | The field's editor UI alias |
| Bindings | — | `[Field(SupportsBindings = true)]` |
| Who may use it | The Commerce section | `RequiredSections` |
| Nesting | A group rule holds child rules | — |

How the framework uses them:

- **Fields** come from `ItemTypeFields.For`, with the same type mapping as model classes. `[Required]` and
  `[AllowedValues]` become part of the schema. Other rules (`[Range]`) go on the field (`FieldDefinition.Rules`) and
  are checked on its path, since there's no class to carry them.
- **Editor UI aliases** map to references through a small table per product, for example Commerce's collection picker
  becomes a reference to `umbraco-commerce-collection`. An editor no table maps stays a plain value, and its
  description names the editor, so it still works with less guidance.
- **Labels** that are localization keys (`#ucDiscountRuleProviders_…Label`, Commerce's default) are treated as
  missing. The type is named by its alias, and its description tells the model so.
- **Bindings.** A setting that supports bindings accepts `${trigger.orderId}` in place of a value. The schema marks it
  with `x-bindings`, and type and reference checks wait until it resolves.
- **Availability.** An item type can say who may use it (`AvailableTo`, here from required sections). The schema offers
  a user only those, and adding one they may not use is refused when it's recorded.
- **In a typed contract** the field is `List<DynamicItem>`: items with a type and JSON fields, items fields included. One mapper
  per seam turns them into the product's stored form (`ProviderItems`: provider alias plus string settings, children
  for groups), using each setting's CLR type.
- **Abilities** can be item types too (`AbilityItemTypes`), so workflow steps are abilities and Automate's actions side by side.

Classes per item type are still an option for types a package owns and wants to describe richly (the CMS page's
blocks), but they are no longer the way to do plugin seams.

What it found:

- **The registries already have enough.** Commerce keeps the CLR type, validation rules, editor alias and sort order
  per setting, and Automate keeps the same on its fields. Nothing had to be added to either model.
- **Localization-key labels cost the model guidance.** Commerce's provider labels and descriptions default to keys only
  the backoffice resolves. Real text on the attributes, or server-side text for those keys, would help.
- **Editor tables are upkeep.** Each product keeps one, and an editor it misses loses its meaning (a store picker
  becomes a plain string).
- **Rule keys still need stable ids** (above). The store picker is left unmapped on purpose, to show the fallback: the
  PoC has no store definition for it to point at.

## Identity: UDIs

An entity's identity is a UDI, as in Deploy and the CMS: `umb://document/{key}`. Entity types are UDI entity types
(`document`, `media`, `umbraco-commerce-discount`), and adding an adapter registers its type with `UdiParser` unless
the CMS or a package already has. Change sets key their snapshots, versions and resolutions by UDI, and the CMS draft
set addresses what it changes by UDI.

Tools still take and return `{ "entityType": "document", "key": "<dashed guid>" }`. A UDI writes its key without
dashes, and the same key in two forms invites copy errors. They also accept a UDI wherever they take a reference, and
store it in the field's own form:

| Field | Given | Stored |
| --- | --- | --- |
| A reference to one type (`[EntityKey("document")]`) | `"umb://document/3f2a…"` | `"3f2a…-…"`, a dashed key |
| A reference to one of several types | `"umb://media/3f2a…"` | `{ "entityType": "media", "key": "3f2a…-…" }` |
| Either, with the wrong type | `"umb://media/3f2a…"` for a document | Refused: "Expected a document, not a media." |

The type names follow each product's Deploy package. Commerce Deploy defines `umbraco-commerce-discount`, but doesn't
register it or deploy discounts today, so the adapter registers it. The sample's collection has no Commerce
equivalent, and `umbraco-automate-workflow` follows the convention but isn't checked against Automate.

Moving to UDIs found a bug: conflict resolutions were keyed by their own `type:key` string rather than the set's
shared identity. They matched only by coincidence, and stopped matching once identity changed. There is now one
identity function.

## Why "field" and "schema"

"Field", not "property", on purpose. "Property" already means a C# property in the typed layer and a CMS property in
the CMS adapter. "Field" is the neutral word between them. Commerce and Automate settings already use `[Field]`.
"Schema", not "structure" or "shape", because the tool is `entity_getSchema` and Microsoft's AI library uses the same word.
