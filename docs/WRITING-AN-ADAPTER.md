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
public sealed class DiscountEditModel : EntityModel   // the key and version; or mark your own [Key] and [EntityVersion]
{
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

public sealed class CmsPage : EntityModel
{
    [Items] public List<PageBlock> Blocks { get; set; } = [];                    // every [ItemType] class that extends PageBlock
    [Items(typeof(RichText), Max = 2)] public List<GridBlock> Left { get; set; } = [];  // narrowed to the classes named
}
```

An items field of classes needs no group name and no registration. Its item types are every `[ItemType]` class that
extends the list's element type, found when the adapter is created. Name classes only to narrow the list.

The class hierarchy only says which classes *could* go in a field. Which ones are allowed is often a setting. In the
CMS, a Block List or Block Grid allows the blocks its data type's configuration lists, and two properties on the same
document type can allow different blocks. There are two ways to say it:

| The allowed list is | Write |
| --- | --- |
| Fixed in code | Name the classes: `[Items(typeof(TwoColumn), typeof(RichText))]` |
| A setting, known only at run time | Override `AllowItems` on the adapter. The list is read each time it's needed, so a changed setting applies straight away |

```csharp
public List<Type> AllowedBlocks { get; } = [typeof(Hero), typeof(CardGrid)];   // stands in for the data type's configuration

protected override void AllowItems(ItemAllowList<Page> allow)
    => allow.Field(x => x.Blocks, () => AllowedBlocks);
```

A type not in the list isn't offered in the schema, and adding one is refused. Items saved before the list changed
still load, and validation reports them. When reading the setting means a database or service call, pass an
asynchronous list instead: `allow.Field(x => x.Blocks, async cancellationToken => ...)`. It's read once per call. `AllowItems` also takes aliases, for a
plugin seam's `DynamicItem` field, and fields of item classes inside the model. The real `CmsDocumentAdapter` builds
its schema from data types, so it goes one step further: each data type has its own item type group
(`cms.blocks:{dataTypeKey}`), and the block editor's handler serves them all (`BlockEditorHandler`, see
[Property editors in the CMS adapter](#property-editors-in-the-cms-adapter)). A source says which groups it serves and
is asked for one at a time, so it can read that group's configuration when a call needs it.

**Fluent configuration**, for a class the package doesn't own or won't annotate, such as a Management API request
model in another assembly. It needs no base class either: items name their own key property
(`Samples/Cms/CmsPage.cs`). It goes in the adapter, like EF Core's `OnModelCreating`, not in global state.

```csharp
protected override void Configure(ModelConfigurator<MediaRequestModel> models) => models
    .Entity(media => media
        .IsEntity("media", "Media")
        .HasKey(x => x.Id)
        .HasVersion(x => x.Version)
        .Ignore(x => x.UpdateDate)
        .Field(x => x.Name).IsName().Required()
        .Items(x => x.Crops))
    .Model<MediaCropRequestModel>(crop => crop
        .IsItemType("crop", "Crop")
        .HasKey(x => x.Id)
        .Field(x => x.Width).Required().Rule(new RangeAttribute(1, 10000)));
```

**The adapter** maps to the domain, checks access, and adds the rules the contract can't express:

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

    public override Task<string?> AuthorizeAsync(EntityAccessRequest request, CancellationToken ct) => /* null: allowed */;

    protected override IEnumerable<DiscountEditModel> ListModels() => discounts.GetDiscounts().Select(ToEditModel);

    protected override void Validate(DiscountEditModel d, ModelProblems<DiscountEditModel> problems)
    {
        if (d.EndDate < d.StartDate) problems.At(x => x.EndDate, "Must be on or after startDate.");
    }
}
```

The access check is required. Registering an adapter without one fails at start-up, as an ability without an access
rule does. Finding by name works from `ListModels`; override `FindModelsAsync` when the product can search itself.

What the package no longer writes:

| Was hand-written | Now comes from |
| --- | --- |
| The schema: fields, types, required, allowed values, references, items fields and their limits | Property types, plus attributes or fluent configuration |
| JSON Schema for structured values (a focal point) | `JsonSchemaExporter` over the property type |
| Culture variation | `Varying<T>` (a value per culture), on value and items fields |
| Block settings and a grid block's layout | Ordinary object properties (`Settings`, `Layout`): a class becomes an object field |
| Turning the contract into the working copy and back | Done both ways, keeping item keys and giving each item its path |
| Range and format checks reported on entity paths (`rewards/{key}/percent`) | DataAnnotations, as attributes or fluent `Rule(...)`, on items and objects at any depth |
| A path for a domain problem | `problems.At(x => x.EndDate, ...)`, or `problems.At(item, r => r.Percent, ...)` for an item |
| Item type groups and registering each item class | The list's element type, or the classes named in `[Items(...)]` |
| An item type source and a settings mapper per plugin seam | `PluginItemTypeSource` and `PluginSettings`: the product only describes one plugin |
| An ability that edits an entity, both directly and inside a change set | `ModelEditAbilityBase<TInput, TModel>`: one `Edit(model, input)` method, or `EditAsync` when it has to look something up. The key is the input's `[EntityKey]` property |

`[Required]` and `[AllowedValues]` become part of the schema, so the generic validator checks them per culture.
A new entity or item starts with each property's default when it isn't given a value: `[DefaultValue]`, or the
property's initializer (`public int? RowSpan { get; set; } = 1;`). An initializer that is only a starting container
(`= []`, an empty string) or a plain `int` or `bool`'s own `0` or `false` isn't a default. A class with no
parameterless constructor needs `[DefaultValue]`.
Every other rule runs on the contract. Each problem is reported once. A renamed property is a compile error, not a
wrong string key at run time.

## What the mapping costs

The Commerce sample now has a domain shaped like the real one (`Samples/Commerce/Domain.cs`). Its read-only entity
has internal constructors and is changed through `AsWritable()`. Dates are UTC `DateTime`s, and rules and rewards
are a provider alias plus string settings, with no ids.

| Part | Statement lines |
| --- | --- |
| Editing contract | 9 |
| Mapping for the discount: load, save, dates, keys | about 35 |
| Domain rules (dates in order, unique code) and finding | about 8 |
| Item types: describe one Commerce provider, and say where the providers are. Once per seam, for every provider, third parties' included | about 12 |
| Provider mapper: a group rule's children. The framework maps alias plus string settings | about 10 |

The mapping is ordinary typed C#, roughly the size of a Management API mapper. It is the only per-entity code the
package writes besides the contract. Before the developer experience fixes in [FINDINGS.md](FINDINGS.md#developer-experience-of-adapters),
the last three rows were about 55 lines. They are now about 30.

**One limit the mapping exposed:** Commerce's rules have no ids, so the adapter derives each item's key from the
discount, the position and the provider alias. A rule's key is stable across loads. But a rule the agent added gets
a new key once saved, and moving a rule changes its key, so rebasing sees a move as a remove and an add. Stable ids
on Commerce's rule nodes would fix this. It is a change to suggest to the Commerce team, not something a mapping can
paper over.

## Entities whose schema is data

A CMS document type is defined in the backoffice. It has no class to describe, so its adapter works over the typed
`Entity`. Umbraco's media and member types are the same kind of thing, so the shared part is a base class,
`ContentTypeEntityAdapterBase<TType>`: it builds the schema from the type, reads and writes values through each
property editor's handler, and maps Umbraco's errors. A product implements only its own part:

```csharp
public sealed class CmsMediaAdapter(CmsContentSchemas schemas, IMediaTypeService mediaTypes, IMediaEditingService editing /* ... */)
    : ContentTypeEntityAdapterBase<IMediaType>(schemas)
{
    public override string EntityType => "media";
    protected override IEnumerable<IMediaType> ListTypes() => mediaTypes.GetAll();
    protected override IMediaType? FindType(string alias) => mediaTypes.Get(alias);
    protected override IMediaType? FindType(Guid key) => mediaTypes.Get(key);
    protected override async Task<StoredContent?> LoadStoredAsync(Guid key, CancellationToken ct)
        => await editing.GetAsync(key) is { } media ? ReadStored(media) : null;

    public override async Task<EntitySummary> SaveAsync(Entity media, CancellationToken ct = default)
    {
        var content = await ToContentAsync(media, ct);   // names, variants and values, ready for the editing service
        // ... CreateAsync or UpdateAsync with content.Variants and content.Properties
    }

    // ValidateAsync (map errors with content.ToProblem), FindAsync, AuthorizeAsync, EditUrl, UnitOfWork
}
```

Give every such adapter the same `CmsContentSchemas`, so each block group's item types are served once.

An Automate workflow looked like the same case, since its step types are
other packages' abilities and plugins. But only its steps vary, so it is a model class with `List<DynamicItem>` steps,
as a discount's rules are. In the CMS adapter there is still no `JsonObject` digging: they use `item.Settings` or `item.Layout`, not
`item["settings"]["fields"]`. The block editor's handler builds Umbraco's own `BlockListValue`, `BlockGridValue`,
`BlockItemData`, `BlockPropertyValue`, `BlockGridLayoutItem` and `BlockItemVariation` instead of hand-built
envelopes, and Umbraco's serializer reads and writes them. Moving to the typed document took it from 621 lines to 599. Each
property's value is its editor's handler's job (see
[Property editors in the CMS adapter](#property-editors-in-the-cms-adapter)), so the adapter itself names no editor.

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

## Property editors in the CMS adapter

In the CMS, a property's value is shaped by its property editor, and anyone can write an editor. So the CMS adapter
knows no editor. Each editor has a handler, `IPropertyEditorEntityHandler`, that turns its values into a field and
back. A property editor's package implements one, and every property using the editor works with the entity tools.

| Method | What it does |
| --- | --- |
| `Handles(editorAlias)` | Says which editor it maps |
| `DescribeAsync(context)` | The field a property becomes. `context` has the property, its data type and configuration, and helpers (`context.Field(type)`, `context.Items(group)`) |
| `ReadAsync(stored, reader)` | The stored value, in editor format, as the field's value. For an items field, an array of items |
| `WriteAsync(value, writer)` | The field's value as the stored value Umbraco saves |

A third party's star rating, stored as `{ "stars": 4, "of": 5 }` (`tests/Umbraco.Abilities.Cms.Tests/StarRatingEditor.cs`):

```csharp
public sealed class StarRatingHandler : IPropertyEditorEntityHandler
{
    public bool Handles(string editorAlias) => editorAlias == "Acme.StarRating";

    public Task<FieldDefinition> DescribeAsync(PropertyEditorContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(context.Field(FieldType.Integer) with
        {
            Description = $"{context.Description} A rating in stars, 1 to 5.",
            Rules = [new RangeAttribute(1, 5)],
        });

    public Task<JsonNode?> ReadAsync(JsonNode stored, PropertyValueReader reader, CancellationToken cancellationToken = default)
        => Task.FromResult(Parse(stored)?["stars"]?.DeepClone()); // Parse: stored JSON comes back as text or an object

    public Task<JsonNode?> WriteAsync(JsonNode value, PropertyValueWriter writer, CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode?>(new JsonObject { ["stars"] = value.DeepClone(), ["of"] = 5 });
}
```

A model now sets a number, and a rating of 7 is a problem on `rating` before Umbraco sees it. Without the handler, the
rating would be a whole JSON value the model has to get right.

What the adapter does, so a handler doesn't:

- **The property.** The field's alias and variation come from the property, after the handler describes it.
- **Cultures and segments.** A handler reads and writes one value; the adapter calls it once per variant.
- **Elements.** An editor whose values hold elements (blocks) asks the adapter to read or write them
  (`reader.ReadElementAsync`, `writer.WriteElementAsync<TValue>`). Each of the element's properties goes to its own
  handler. Values are Umbraco's own `ValueModelBase`, and the editor picks its value model: the block handler asks for
  `BlockPropertyValue`, so nothing in the contract is about blocks.
- **Saving and validating.** Through `IContentEditingService`, as before.

Two optional parts, for editors that need them:

- **Error paths.** Umbraco reports an error inside a value as a JSON path into it. `writer.LocateErrorsWith(...)` says
  how to turn that into an entity path. Without it, the error is on the property, with the JSON path.
- **Item types.** A handler whose field holds items also implements `IPropertyEditorItemTypes`: which groups it serves,
  and their item types now. The block handler reads the data type's allowed blocks each call.

| Handler | Editors |
| --- | --- |
| `ValueEditorHandler` | An editor whose stored value is the field's value. One line per editor: `new ValueEditorHandler("My.Editor", c => c.Field(FieldType.String))`. Used for the core text, number, boolean, tags and content picker editors |
| `BlockEditorHandler` | Block List and Block Grid |
| `JsonValueEditorHandler` | The fallback: any other editor is a whole JSON value, described by Umbraco's value schema for it |

Handlers are found on their own: the interface is `IDiscoverable`, and `PropertyEditorEntityHandlersComposer` adds
every one in the scanned assemblies to `PropertyEditorEntityHandlerCollection`, as Umbraco.AI finds its handlers. Use
`builder.PropertyEditorEntityHandlers()` to add or exclude one. Packages' handlers are asked before the core ones, so a
package can also map a core editor its own way.

**Defaults.** Set `Default` on the field you describe to what the editor starts a new value at, from its data type's
settings (`context.Setting("min")`). The core toggle and number handlers match the backoffice's value presets. The
framework fills it in when a document is created or a block added.

**Write back everything you read.** When an edit touches a property, the adapter rewrites its whole value from the
field. Anything the handler dropped on read is lost. Keep product data that isn't a field under a `$` name on the
value, as the block handler keeps a block's settings key (`$settingsKey`). Where the contract
should live (CMS core, Umbraco.AI or here) is open: see
[FINDINGS](FINDINGS.md#where-property-editor-handlers-should-live).

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
- **Availability.** An item type can say who may use it (`AvailableTo`, here from required sections). It can wait,
  since a product usually looks the user up. The schema offers
  a user only those, and adding one they may not use is refused when it's recorded.
- **In a typed contract** the field is `List<DynamicItem>`: items with a type and JSON fields, items fields included.
  `PluginSettings` turns them into the most common stored form (provider alias plus string settings), using each
  setting's CLR type. A product adds only what's its own, such as a group rule's children.
- **Abilities** can be item types too (`AbilityItemTypes`, through a plain `ItemTypeSource`), so workflow steps are
  abilities and Automate's actions side by side.

What a product writes per seam is one method that describes a plugin, and one line that says where its plugins are:

```csharp
protected override IEnumerable<IItemTypeSource> ItemTypeSources() =>
[
    new PluginItemTypeSource(RuleGroup, () => ruleProviders.Select(Describe), Editors),
];

private static PluginDescriptor Describe(DiscountProviderBase provider) => new(
    provider.Alias, provider.Label, provider.Description,
    provider.SettingDefinitions.Select(s => new SettingDescriptor(s.Key, s.Label, s.Description, s.PropertyType, ...)).ToList());
```

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
