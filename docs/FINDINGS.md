# Findings

> Part of the [Abilities proof of concept](../README.md). What building it taught us, what running it against a real
> Umbraco and a real model showed, and where the real product models still don't fit. Findings that change the design
> are folded into [ARCHITECTURE.md](ARCHITECTURE.md).

## What building it taught us

These feed back into the design in [ARCHITECTURE.md](ARCHITECTURE.md).

1. **Approval depends on the call, not just the tool.** `email.send` and `commerce.activateDiscount` need
   approval when run directly. Inside a change set they are only recorded, so asking then is pointless; the
   approval belongs on `changeSet.apply`. M.E.AI's `ApprovalRequiredAIFunction` wraps a tool once, so the
   adapter can't express "only outside a set". The contract needs approval decided per call, from the
   context.
2. **Side effects are not checked when recorded.** An `email.send` with a bad recipient is
   accepted into the set and only fails at apply. Abilities need an optional "check this input without
   running it" hook, so a set can be validated fully before anyone approves it.
3. **Every read must be draft-aware.** `entity.find` first missed an existing entity renamed in the set.
   The generic reads now use the working state. A product's own Read ability reads through the change set too: it
   runs its own query for what's live, then `ChangeSetService.ReadAllAsync` (or `ReadModelsAsync` for a typed model)
   adds what the current set changed, created or deleted. `commerce.listDiscounts` shows a discount renamed, switched
   on and created in the set. This is the generic form of the CMS PoC's read overlay. Products filter after reading,
   since what a set built may not match their own query.
4. **Caller-chosen keys solve references between new items.** Writing generated keys into the recorded
   input made replay deterministic, and let a discount refer to a page created in the same set, and a
   workflow step refer to a later step. No key remapping was needed.
   That holds only where the product stores item ids. Commerce's rules have none, so their keys are derived
   on load and change once saved (see [What the mapping costs](WRITING-AN-ADAPTER.md#what-the-mapping-costs)).
5. **Problem paths that equal edit paths matter.** Because `rewards/{key}/percent` is both what
   validation reports and what `entity.setValue` takes as its `path`, the fix loop needs no
   translation.
6. **Abilities as item types works.** Reflecting an ability's input type into fields made Automate's step
   types come for free, including an entity reference from `[EntityKey]`.
7. **Typed values found an untyped bug.** The validator checked whole numbers with
   `JsonValue.TryGetValue<long>`. That works on parsed JSON but fails on a value holding a CLR `int`, which
   is what the CMS adapter puts in once it reads Umbraco's typed `BlockGridLayoutItem.ColumnSpan`. Every
   grid page then failed validation. The check now reads the JSON text. A typed document must not assume its
   values came from parsing.
8. **Slots were fields all along.** The schema first had fields and, separately, slots: lists of typed items. But
   every existing model treats "a list of typed things" as a field with that value type: Umbraco's Block List is a
   property like any other, JSON Schema has an array of a `oneOf`, GraphQL a list of a union. So there is now one concept,
   a field, with value type `Items` (its item type group, min, max and placement values; a single block is `Max = 1`).
   What items add are properties of that value type: each item has a key, so paths (`rules/{key}/minTotal`), problems
   and rebasing work per item; the set of item types is open; and items are edited by adding, removing and moving.
   Paths didn't change. The schema has one `fields` object, where an items field lists its `x-item-types`; tools take
   a `fieldPath`; the typed layer has `[Items]` and fluent `.Items(...)`. It also removes a question that only existed
   because of the split: whether a property editor is a field or a slot. An editor declares its value type, and
   items are one value type.
9. **Values are their editor's model.** Items as a field type still assumed items sit directly in a field, and the
   Block Grid's placement values leaked into the language. But a CMS property can hold anything, and only its editor
   knows the model inside, which is how Umbraco.AI's update tools already work. So a value type can have a handler that
   owns its inner paths, operations, rules and problems, and the framework routes the same tools to it. Keyed items stay
   as the built-in value type for fixed models such as discounts. Umbraco 18 describes every core editor tried here
   with a value schema, including a date (`date-time`) and a dropdown (an array of strings). So the "no schema at all"
   fallback is mainly for third parties' editors.
10. **One value tree, and paths through values.** Finding 9 still stored items apart from other values, and handlers
   owned paths. Asking what consumers of the entity tools need showed neither was required: a path goes down through
   values, object steps by field and item steps by key, and the schema says which values vary, hold items or hold
   objects. So the working copy became one JSON tree, a block's settings and a grid block's layout became ordinary
   object fields (`@settings` and `@layout` are gone), handlers only add behaviour (Rich Text's tags), and the
   separate walks over items in the editor, diff, validation and authorization became one. It also removed guessing
   which JSON is items. Left out on purpose: a list of which variants an entity "has". Consumers write values per
   culture and see them per culture; which cultures exist is product meaning worked out from the values (the CMS:
   cultures with a name), so it isn't part of the contract.
11. **Variation is named dimensions, not culture.** The framework had used the CMS's `ContentVariation`, so values
   could only vary by culture and segment. Now an entity's schema lists its dimensions (`culture` with the site's
   languages; `segment`, open; or a product's own, such as `currency`), each field says which it `VariesBy`, tools take
   a `variant` object (`{ "culture": "da-DK" }`), and problems and conflicts carry one. A dimension with listed values
   needs one of them; an open one may be left out for its default. A required value that varies but has no value in
   any variant is reported, which covers "the page has no culture yet" without a culture rule. The CMS adapter maps
   `ContentVariation` to its two dimensions; a typed model's `Varying<T>` is per culture unless `[VariesBy(...)]` says
   otherwise.
12. **Which change set a call works in is ambient, and named per call.** A real model wasn't told the id of the set it
   worked in, so it couldn't validate its work. Looking closer, a set the model created itself couldn't be used at all:
   `changeSet.create` returned an id, but nothing passed it on to later calls. The scripted tests hid this by copying
   the id by hand. Now it works like the CMS Draft Sets proof of concept: an accessor holds the current set for one
   call, per request, and nothing else carries it. No ability input has a change set id. The AI adapter adds an
   optional `changeSetId` to every tool, so a call can name a set. Leave it out and the call uses the only set open in
   the conversation. With two or more open, a call that names none is refused and told which are open: using the last
   one touched would let a model that forgot to switch back edit the wrong set, with nothing to say so. Every result
   says which set it used, and a set that doesn't exist is refused rather than falling back to live data. An ability
   whose result can't depend on a set (getting a schema, starting a set) says `UsesChangeSet = false`: it takes no id
   and always runs outside one. A Write can't say it, since it would change live data. For HTTP callers, a middleware
   reads the `Umb-Change-Set` header into the same accessor.
13. **A step's exits are fields, one per outcome.** Automate stores a workflow's links as a list of connections, each
   from one step to another with an optional outcome (`true` and `false` for an If; any action can name its own on
   Automate's `action-outcomes` branch). Three ways to show this to a model were weighed: a list of connections as
   stored, one field per exit on each step, or the exits nested in an `outcomes` object. A list of connections needs
   Automate-only code to check that an outcome is one the step has, and the schema can only describe outcomes in text.
   Exits as fields are checked by the generic code, and a wrong name is refused on the spot with the right names
   listed. Nesting them keeps settings and exits apart. So a step with one exit has `next`, and a step that names
   outcomes has `outcomes` and no `next`: one way to route per step, so a model can't send every result to a step it
   meant for one. The adapter converts to Automate's connections on save and back on load. Building it found a gap:
   an object set whole wasn't checked for names it doesn't have, while fields set one by one were. Now both are.
14. **Most of the CMS adapter is about a kind of model, not about the CMS.** The CMS document adapter and its schema
   mapper are about 1,000 lines. Only about a tenth deals with how the CMS itself behaves (an update replaces every
   value, invalid values are saved and reported afterwards, every culture is validated). The rest belongs to any model
   whose **types are defined by editors at run time** and whose **values are shaped by their property editors**: media
   and member types, element types, and likely Umbraco Forms. A document type is a plugin seam like discount rules:
   properties are the slots, and property editors are the plugins. The typed layer only helps models that are C#
   classes, so the CMS adapter had to do all of it alone. See
   [Runtime-defined models](#runtime-defined-models) below.
15. **A model with one varying part is still a typed model.** The Automate workflow was written against raw entities,
   as the escape hatch for a schema that is data. But only its steps vary; its name, switch and trigger don't. As a
   model class with `List<DynamicItem>` steps it lost its hand-written schema, and its save and load now map typed
   connections. The escape hatch is for models where the whole shape is data, such as a document type.
16. **Anything a product looks up has to be able to wait.** The samples keep everything in memory, so several seams
   were synchronous and nothing showed it. A real product reads data types, users and plugin settings from a database.
   These are now asynchronous:

   | Seam | Why a product would wait | Now |
   | --- | --- | --- |
   | Item type sources, allow lists, who may use a type | Reading a data type's allowed blocks, a user's sections | Read once at the start of each call (`ItemTypeRegistry.ResolveAsync`), only for the groups the entity's schema uses. The rest of the call looks types up in the result, `ItemTypes`, without waiting, so walking, diffing and validating values stay simple |
   | A product's edit (`ModelEditAbilityBase`) | Looking up a price or a related entity | `EditAsync`, beside `Edit` |
   | A new entity | Default values from a document type | `NewAsync` |
   | A product's transaction | EF Core's transactions are asynchronous | `BeginAsync`, `CompleteAsync`, `IAsyncDisposable` |

   Still synchronous, on purpose: `ToSummary` (it reads the name from the entity; an edit URL that needs a lookup, such
   as one with a store in it, would need it too), value handlers (they rearrange a value; settings they need could come
   with the resolved item types), `Prepare` (it only picks keys), and the optional hooks that have an async twin
   (`Validate`, `ListModels`). Resolving item types on every call reads the sources each time; a real product would
   cache them and clear the cache when a plugin or data type changes.
17. **Item types can depend on configuration, not just on what's installed.** A Block List allows the blocks its data
   type lists, so two properties can allow different blocks, and an editor can change the list at any time. The CMS
   adapter used to add each data type's blocks to the registry while it built a schema, and never removed them: a block
   taken out of a data type stayed allowed until a restart. Now a source says which groups it serves
   (`IItemTypeSource.Serves`) and is asked for one group at a time. The CMS has one source for every
   `cms.blocks:{dataTypeKey}` group, which reads that data type when a call needs it. Each call asks only for the groups
   its entity's schema uses, then the groups those blocks use, so it is always current and reads nothing else. A page
   that already holds a block its data type no longer allows still loads and can still be edited; validation names the
   block. Its settings are kept, but a grid block's areas are not, since the configuration no longer names them.
18. **Each property editor maps its own values.** The CMS schema mapper had a `switch` on editor aliases, and the
   adapter held all the Block List and Block Grid code. So a third party's editor could only ever be a whole JSON value.
   Now each editor has a handler (`IPropertyEditorEntityHandler`) that does three things: describes the field a
   property becomes, reads the stored value into the field's value, and writes it back. A handler can also say how to
   turn Umbraco's error paths inside its value into entity paths, and serve the item types of the groups it names.
   The core editors moved onto it, and the adapter and mapper no longer name any editor:

   | Handler | Editors | Size |
   | --- | --- | --- |
   | `ValueEditorHandler` | Text box, text area, integer, decimal, boolean, tags, content picker | One line each |
   | `BlockEditorHandler` | Block List and Block Grid: the group per data type, its item types, reading and writing block values, error paths | ~380 lines, moved out of the adapter and mapper |
   | `JsonValueEditorHandler` | Every editor no handler takes: a whole JSON value with Umbraco's value schema | The fallback |
   | `StarRatingHandler` (test project) | A third party's editor, stored as `{ "stars": 4, "of": 5 }` | ~25 lines. Its field is a number from 1 to 5, checked before Umbraco sees it |

   The adapter went from about 770 lines to about 550, and the mapper from 250 to 80. Building it taught four things:
   - **Elements go back through the adapter.** A block's values are properties too, each with its own editor. So a
     handler never maps an element's properties itself: it asks the adapter to read or write them
     (`ReadElementAsync`, `WriteElementAsync`), and each property goes to its own handler. A star rating inside a
     block works with no extra code.
   - **Error paths chain.** Umbraco reports an error inside a block in a block as one long JSON path. The block
     handler finds the block, then hands the rest of the path to the inner property's locator. Each handler only
     knows its own format.
   - **The property stays the CMS's.** The adapter sets the field's alias and variation from the property after the
     handler describes it, so a handler can't get them wrong. Cultures, segments and saving stay in the adapter.
   - **Item types need element schemas, which need handlers.** Building a block's item type maps its element type,
     whose properties need handlers. Passing the element schemas into the call (`IElementSchemas`), instead of
     giving the handler the mapper, keeps the handlers free of that loop.

   Where the handlers should live in the end is open. See
   [Where property editor handlers should live](#where-property-editor-handlers-should-live).
19. **A new value's default is its editor's, and the CMS only knows it in the browser.** A new block's toggle was
   saved empty, where the backoffice would have started it at its preset. Umbraco keeps these presets in the
   backoffice only (`propertyValuePreset`, one per editor: a toggle starts at its `default` setting or off, a number
   at its `min` setting or 0), and has no server-side way to ask for them. Umbraco.AI hit the same gap: its
   `IAIPropertyDefaultValueProvider` returns nothing for every property until the CMS has one. A field now has a
   `Default`. It is in the schema as JSON Schema's `default`, and filled in when an entity is created or an item
   added, inside object fields too (a block's settings). A varying field gets it only in the call's variant. The
   property editor handler is where a CMS default belongs: the core toggle and number handlers read the same
   settings the backoffice presets read. A typed model uses `[DefaultValue]` or a plain C# initializer
   (`= 1`), read from one instance of the class. If the CMS adds a server-side preset, the handlers would call it
   instead. One effect: a new grid block now starts with a layout holding its default row span, so a missing layout is
   reported at its missing column span, which is the more precise path.
20. **Types that are data need one base class, not one adapter each.** The document adapter held the schema, reading
   and writing values, variants and segments, and mapping Umbraco's errors. Media and members need all of it too,
   since their types are content types with the same property editors. Now `ContentTypeEntityAdapterBase<TType>` holds
   it, and a product only says what is its own:

   | The base does | The product does |
   | --- | --- |
   | Lists types as subtypes, builds the schema (a name, a parent, each property by its editor's handler) | Lists and finds its types (`ListTypes`, `FindType`) |
   | Reads stored values into an entity (`ToEntityAsync`), and an item from Umbraco in editor format (`ReadStored`) | Loads the item (`LoadStoredAsync`) |
   | Writes an entity as the editing services take it: names as variants, segments, values (`ToContentAsync`) | Validates and saves through its own editing service |
   | Maps Umbraco's validation errors to entity paths (`ContentValues.ToProblem`) | Checks access with its own permissions |
   | Finds the values an edit touched (`TouchedValuesAsync`), for products that stage per value | Drafts, if it has them |

   `CmsMediaAdapter` is the proof: about 130 lines, and it got blocks, nested blocks, error paths inside blocks,
   defaults and third-party editors with no code of its own. The document adapter went from about 550 lines to 250.
   Building it taught three things:
   - **What adapters share has to be made once.** Each adapter registered its own item type source, so two adapters
     would serve every block group twice. `CmsContentSchemas` now holds the handlers and the schema, and each adapter
     takes the same one.
   - **The two apply models sit side by side.** Documents stage into a CMS draft set; media has no drafts, so the
     change set keeps and replays its edits and calls `SaveAsync` on apply. The base doesn't care which.
   - **"Types are data" stops at Umbraco's content types.** The handler contract is written against a property type
     and a data type. Umbraco Forms has field types and their settings instead. Covering it means describing a
     property in neutral terms (alias, name, mandatory, editor, settings) and letting each product map its own. Not
     built: there is no Forms adapter to prove it against.

## Developer experience of adapters

Making things easy for a model is one side. The other is what a package developer writes. Reading the samples as
package code found these costs:

| Cost | Fix | Status |
| --- | --- | --- |
| An items field needed a group string, a constant and a separate `RegisterItemTypes` call per item class | Work out the item types from the list's element type: every `[ItemType]` class that extends it. Name classes only to narrow the list | Done |
| Every product with a provider registry wrote the same item type source and the same "alias plus string settings" mapper (about 90 lines for Commerce) | `PluginItemTypeSource`, `PluginDescriptor` and `PluginSettings` in the framework. A product describes one plugin and says where its plugins are | Done. Commerce and Automate both use them |
| An adapter with no access check silently allowed everything | Required when the adapter is registered, as Abilities requires an access rule on every ability | Done |
| Fluent configuration lived in a static constructor, as global state | An overridable `Configure(models)` on the adapter, like EF Core's `OnModelCreating` | Done |
| Wiring an adapter took three separate calls | The adapter registers its own item types and item type sources when it's created. Adding it to the registry is the one call left | Done |
| The workflow adapter dug through raw JSON, although only its steps vary | A typed model with `List<DynamicItem>` steps, as discounts do. The untyped in-memory base class is gone | Done |
| Optional methods needed `Task.FromResult<IReadOnlyList<…>>` wrapping | A problem collector (`problems.At(d => d.EndDate, "…")`) in a plain `Validate`, and finding from `ListModels()` | Done |
| `ModelEditAbilityBase` asked for `KeyOf(input)` | Found from the input property marked `[EntityKey]` for the adapter's entity type | Done |
| Entities repeated `[Key]` and `[EntityVersion]`, while items have `EntityItemModel` | An optional `EntityModel` base class | Done |
| Every save starts with "load it, or create it if missing" | Split into create and update in the base class | Not needed. In practice it is one line (`GetDiscount(...)?.AsWritable() ?? CreateDiscount(...)`), and a split would add a method |
| Descriptions are written as `[Description]`, next to doc comments | Fall back to `/// <summary>` | Not done. Needs the XML doc file shipped, or a source generator |
| A missing `[Key]` or an unsupported property type fails at start-up | Build-time checks from a source generator, which could also replace the reflection in `ModelSchemas` | Not done. Start-up errors now say what to add |
| Testing an adapter needs the framework's own test harness | Ship `ScriptedAgent` and a "load, change, save and compare" check | Not done |

Measured on the Commerce sample, the item types, mapper and domain rules went from about 55 statement lines to about
30. The editing contract lost its key and version lines. Automate's workflow lost its hand-written schema, and its
save and load now work on typed connections.

### Runtime-defined models

How the CMS document adapter's code splits, and who else has each part:

| Part | Lines (rough) | Who else has it | Generic fix |
| --- | --- | --- | --- |
| Building the schema from document types and data types | ~270 | Anything editors define: media types, member types, likely Forms | Done for Umbraco's content types: `ContentTypeEntityAdapterBase` (finding 20). Media uses it |
| Reading and writing block values (layout, contentData, settingsData, expose) | ~260 | Any product storing property editor values: element types, UI Builder | Done: a handler per editor (finding 18). The block code now lives in `BlockEditorHandler` |
| Turning Umbraco's error paths back into entity paths | ~90 | Any product that validates in its own path format | Done: each handler locates errors in its own value, and hands nested paths on |
| Plugging into CMS draft sets | ~90 | Any product with its own drafts | Already generic: `IDraftingEntityAdapter` |
| Permissions | ~50 | Any product with its own permission model | Already generic: `IAuthorizingEntityAdapter` |
| CMS behaviour: update replaces every value, invalid values saved then reported, every culture validated | ~100 | Only the CMS | Belongs in CMS core: see the Draft Sets checklist in ARCHITECTURE |

Automate shows a smaller form of the same thing: it stores steps and connections as separate lists joined by keys,
and its adapter converts them on load and save.

Two parts are not about the CMS at all. They are about any model whose types are data. What would help, and what
is not built yet:

1. **Value handlers per editor.** Built (finding 18). A handler knows one property editor's stored value: how to
   describe it, read it into fields and items, write it back, and turn the editor's error paths into entity paths. It
   also owns what the editor allows, since that is editor configuration: the block handler reads a data type's
   allowed blocks per call. Element types and UI Builder could reuse the same handlers. Still open: where they live.
2. **A base class for "types are data".** Built for Umbraco's content types (finding 20). The product lists its
   types and loads and saves; the base builds the schema with the value handlers. Forms would need a neutral
   description of a property first.

With both, a CMS-like adapter is its load, save, drafts and permissions, as the typed adapters are: the media adapter
shows it. The CMS behaviour row stays: it is a change for CMS core, not for adapters.

### Where property editor handlers should live

The handlers are built in this repo, in `Umbraco.Abilities.Cms/PropertyEditors`. For a property editor's developer
to ship one and have it "just work" everywhere, it has to live somewhere every consumer can see. Three places:

| Place | For | Against |
| --- | --- | --- |
| CMS core, beside the property editor | A property editor already owns its value editor and its value schema (`IPropertyEditorSchemaService`). A handler is the next part of the same contract. Every consumer gets it: Abilities, Umbraco.AI, imports, Deploy-like tools. An editor's package ships one thing | A core change: it goes through Umbraco's RFC process on GitHub, and once public it is bound by semver |
| Umbraco.AI | It already has one handler per editor, `IAIPropertyValueHandler`, for building values step by step on a path | Abilities isn't AI specific, and must not depend on the AI packages. Editors would implement an AI interface to work with anything else |
| This framework (`Umbraco.Abilities.Cms`) | Where it is now. No one else has to agree | Only the abilities see it. An editor's package would need a reference to Abilities |

The recommendation is CMS core in the long run, through an RFC, with this repo as the place to settle the contract
first.

#### Compared with Umbraco.AI's handlers

Checked against Umbraco.AI's code (`Umbraco.AI/src/Umbraco.AI.Core/PropertyValueOperations/`). It has two contracts
per editor. `IAIPropertyValueHandler` edits the stored value in place: add, remove and move an item, clear the value,
get and set a property inside an item, and optional checks before each change. It has handlers for Block List, Block
Grid, Media Picker, Multi URL Picker and Rich Text. `IAISimplifiedPropertyValueTransformer` gives a model a simpler
schema for an editor (Rich Text as a markup string) and turns the model's value into the stored one.

| Need | Umbraco.AI | Here |
| --- | --- | --- |
| Edit inside a value | Each handler implements every edit on its stored format | A handler reads and writes. The framework's edits (`entity.addItem`, `setValue` at a path) work on the field, the same for every editor |
| A simpler schema for a model | A separate transformer, one way: the model's value to the stored value | `DescribeAsync`, with `ReadAsync` as the way back, so a model also reads the simple form |
| Umbraco's errors inside a value | Not handled | The handler locates them, and nested values hand on |
| Checks before a change | `ValidateAddItem`, `ValidateRemoveItem`, `ValidateSetValue` | The field's rules and the schema check. A check that needs code would be an `IValueHandler` on the field |
| Default values for a new item | `IAIPropertyDefaultValueProvider`, which returns nothing yet: the CMS has no server-side defaults | A field's `Default`, which the handler sets from the same settings the backoffice presets read (finding 19) |
| Finding handlers | `IDiscoverable` and a lazy collection builder: a package ships a handler and it is found | The same, taken from Umbraco.AI |
| Data a handler doesn't understand | Never touched: edits are in place | Lost if the handler drops it on read. Only the properties an edit touches are rewritten, since the draft set stages changed properties only, but those are rewritten whole |

So this contract covers Umbraco.AI's use case by a different route: one read and one write per editor instead of
six edits, and the simple schema and the way back in one place. What it costs is that a handler must be lossless.
Umbraco.AI's handlers could become one adapter over these: read, edit with the shared editor, write.

Taken from Umbraco.AI: handlers are `IDiscoverable` and found by a composer
(`PropertyEditorEntityHandlersComposer`). The star rating test registers nothing; its handler is found on its own.
Default values for new items are built too (finding 19), past what Umbraco.AI does today.

Not taken yet, and worth taking:

- **A round-trip check for handlers.** A test kit that reads a stored value, writes it back, and compares, so a
  handler's author finds a lossy handler before an editor does.

The handler here is separate from the framework's own `IValueHandler`. That one adds behaviour inside a value
(Rich Text keeps its markup and blocks in step) and knows nothing about storage. A property editor handler can set
one on the field it describes.

## What running against Umbraco taught us

`CmsDocumentAdapter` is a real adapter for documents. One entity type, `document`, has a subtype per
document type. It builds the schema from the document type and its data types (Block List and Block Grid
configuration, settings element types, areas, variance), loads through `IContentEditingService` in the editor
format, writes block envelopes with layout, settings, grid areas and expose entries, validates with Umbraco's
own `ValidateCreateAsync` / `ValidateUpdateAsync`, and saves with `ContentCreateModel` / `ContentUpdateModel`
inside one Umbraco scope.

The tests that show it are listed in [TESTS.md](TESTS.md#against-a-real-umbraco).

1. **Schema can depend on a subtype.** Documents are one entity type with a schema per document type,
   chosen at create. The contract gained `GetSubtypesAsync`, `GetSchemaAsync(subtype)` and `subtype` on create
   and schema. Media types and member types will need the same.
2. **An item exists only in the cultures it has values for.** Umbraco exposes a block in the cultures it has
   values in, and skips validating it elsewhere: an English-only hero is not part of the Danish page. The
   generic validator first flagged its missing Danish heading; it now follows the same rule (or every culture
   when an item has no values yet).
3. **Umbraco's block error paths need translating.** Errors inside an invariant block property carry no
   culture; it sits in a JSON path filter, `.contentData[1].values[?(@.alias == 'heading' && @.culture ==
   'en-US' ...)].value`, and `contentData[i]` counts only the blocks exposed in that culture. The adapter
   maps these to `blocks/{key}/heading` with culture `en-US`.
4. **Umbraco saves invalid values and reports them afterwards.** The adapter treats any reported error
   as a failed save, so the scope rolls back and nothing half-valid is applied.
5. **`UpdateDate` works to notice a change.** Umbraco has no check of its own on update; comparing `UpdateDate` tells a
   change set when to rebase.
6. **A segment exists where it has values.** Umbraco has no list of segments. It checks the segments the save names,
   in each culture with page values in that segment, and an empty segment value does not fall back to the default
   (`release-18.0.0`: `ContentValidationServiceBase`). The adapter first named no segments, so Umbraco checked
   none. It now names each segment the page's own values are in. The generic validator first treated a VIP value
   inside a block as making the whole page VIP, so it asked for a VIP heading. It now counts an open dimension, such
   as a segment, only where an object has values of its own. A value that varies by segment only has no culture,
   but Umbraco wants a culture on every variant of a culture-variant page, so the adapter names that segment in
   each of the page's cultures.

Not covered yet: templates (an update would clear one), publishing, rich text with blocks, culture-variant block
properties against real Umbraco, a parent created in the same set, a third party's editor inside a block, and saving
most editors that pass through as JSON (only the eyedropper is saved in a test), and defaults for editors other than
the toggle and numbers.

## Against a real model

`tools/Umbraco.Abilities.ModelRun` gives Claude Opus 5.5 the generic tools and a description, and nothing else. Its
prompt names no product, field or item type. The model creates its own change set; applying is kept for a person.
Runs on 16-11-2026, against the sample Commerce:

| Description | Result | Tool calls | Time | Tokens in / out |
| --- | --- | --- | --- | --- |
| 10% off the Summer collection for VIP customers in December | Correct: VIP rule, Summer collection rule, 10% off, 01-12-2026 to 31-12-2026. Valid, applied | 10 (1 failed) | 20 s | 54k / 1.9k |
| Weekend deal in January: 15% off orders over 500 for customers in the VIP or Gold group, Saturdays and Sundays only | Built the VIP version with an order total rule and the third party's weekday rule. Said there is no Gold group and asked, instead of inventing one. Valid, applied | 10 (2 failed) | 23 s | 48k / 2.1k |
| 10% off the Summer collection for VIP customers in December, with no change set open at the start | Created a change set, built the same discount in it, validated and summarized it with `{}`. Valid, applied | 10 (0 failed) | 16 s | 62k / 1.6k |

What it showed:

- **The schema and tools were enough.** The model read the schema, fetched only the item types it needed, found the
  collection by name, built the discount in small steps and validated it. It found the third party's rule type on its
  own.
- **It reported gaps instead of hiding them.** It said the 10% comes off the whole order, because no reward type limits
  it to the Summer items. It said it couldn't tell whether "over 500" includes 500. It left the discount switched off
  and said so.
- **It needed the change set's id** and wasn't given it. The first two runs had the id in the prompt as a workaround.
  See finding 12 in [What building it taught us](#what-building-it-taught-us): the set is now ambient per call and named per call. In the third run
  the model started with no set, created one, and every later call used it without the id.
- **A fixed list of values reads like a reference.** Each run, the model first looked up customer groups as an entity
  type, and the error listing the known types sent it back. A field's allowed values are in the schema, but nothing
  says the field isn't a reference to look up.
- **Derived keys change on save.** The applied discount's rules have different keys from the ones the model was given,
  as described in [What the mapping costs](WRITING-AN-ADAPTER.md#what-the-mapping-costs).

Not covered: nested rule groups (the model would have used one for "VIP or Gold"), editing an existing entity,
rebasing, CMS documents, Automate workflows, and other models.

## Checked against the real models

The schema language was compared with the real code of Umbraco CMS (`main`, 18.5.0-rc),
Umbraco.Commerce (`dev`) and Umbraco.Automate (`v18/dev`). The CMS sample now follows the real content
model's shapes. What the contract gained from that:

| Added to the schema language | Why (real model) |
| --- | --- |
| Dimensions on the entity (`VariationDimension`, with their values) and `VariesBy` on each field, with `VariantId` narrowed per value | CMS property values carry a culture or segment only if their property varies that way, and Umbraco rejects values that don't match. Other products vary by their own dimensions (a price per currency) |
| A varying value stored per variant where the field is (`"title": { "en-US": … }`), names as a culture-variant field | CMS names vary by culture; block values inside an invariant block property vary per culture |
| Culture-variant items fields (a list per culture) | A culture-variant block property holds one whole envelope per culture |
| `Variant` on `Problem` and on conflicts | The CMS's `PropertyValidationError` reports alias, culture and segment |
| Object fields (`FieldType.Object`): a block's `settings`, a grid block's `layout` | Blocks have a settings element type next to their content type; a Block Grid block has a column and row span |
| `ItemTypeDefinition.AlsoIn` | Grid blocks can be allowed at the root and in areas |
| `Many`, `ReferencesAnyOf`, `FieldType.Json` | Tags and dropdowns are string arrays; a tree picker points at documents or media; focal points, crops and date-times are objects |

What still does not fit, and needs a decision rather than more code:

| Product | Gap | Consequence |
| --- | --- | --- |
| CMS | Blocks in rich text are placed by markup, not by item order | Adding a block to rich text needs an anchor in the markup; editing existing ones fits |
| CMS | Segments are not managed (no list to validate against); "expose" (which cultures a block exists in) is derived by the CMS on read | The CMS adapter must produce expose entries and accept any segment |
| CMS | Update replaces all properties, and saves invalid values while returning errors | The adapter must send the full working copy and treat returned errors as a failed apply |
| CMS | No concurrency token on update | Change sets rebase instead (see [Rebasing](CHANGE-SETS.md#rebasing)); a change saved between validating and saving is still not caught |
| Commerce | A discount's rules are one recursive tree under a group rule (AND/OR is a setting); rule and reward nodes have no ids | Fits as an items field of one group rule with nested items fields, but the adapter must assign item keys on load and drop them on save |
| Commerce | Validation is message-only and stops at the first error; rule and reward settings are not validated on write | Problems by path have to come from the adapter, not from Commerce |
| Commerce | Saves use Commerce's own unit of work and connection, and can't join an Umbraco scope | No longer a gap: change sets don't assume a transaction across products (see [Applying a set](CHANGE-SETS.md#applying-a-set)) |
| Commerce | `Version` exists but isn't exposed, and conflicts are retried (last writer wins) | No change detection for discounts without a Commerce change |
| Automate | Steps are a flat list and edges are their own list carrying data (outcome, handle, filter); loops and parallel paths come from edges, not nesting | Item references on fields are not enough: the contract needs **edges as a first-class part of an entity** |
| Automate | Validation errors are strings with no path; unknown step types aren't caught at publish | Problems by path have to come from the adapter |
| Automate | Bindings (`${ trigger.x }`) resolve to strings | Typed step inputs from bindings need an Automate change |

Two existing Umbraco.AI defects turned up on the way and were raised as issues:
[#746](https://github.com/umbraco/Umbraco.AI/issues/746) (Multi URL Picker links saved without a target)
and [#747](https://github.com/umbraco/Umbraco.AI/issues/747) (block settings use the content element type).

## What the CMS already gives us

Before building something, check whether the CMS has it. This is where the PoC landed:

| Need | Uses | Notes |
| --- | --- | --- |
| Entity identity | UDIs (`GuidUdi`, `UdiParser`) | See [Identity: UDIs](WRITING-AN-ADAPTER.md#identity-udis) |
| What a value varies by | `ContentVariation`, in the CMS adapter only | The framework has named dimensions (`VariationDimension`); the CMS adapter maps `ContentVariation` to its `culture` and `segment` dimensions. Using `ContentVariation` in the framework had made culture and segment the only ways to vary |
| Block editor values | `BlockListValue`, `BlockGridValue`, `BlockItemData`, … | Read and written with Umbraco's serializer |
| A product's transaction, for content | `ICoreScopeProvider` | Behind `IUnitOfWorkProvider`, which stays because Commerce and Automate have their own connections |
| Packages reacting to, or vetoing, an apply | Notifications through `IEventAggregator` | `ChangeSetApplyingNotification` and `DraftSetApplyingNotification` are cancelable, like `ContentSavingNotification`. Then applied and discarded |
| A record of who applied what | The audit log | The draft set writes an entry per document it applies, inside its scope |
| Work after a transaction commits | Scopes: notifications published through `scope.Notifications`, and `Context.Enlist` actions | Both run only when the outermost scope commits. The draft set's applied notification and its "applied" flag use them, so neither happens when an outer scope rolls back |
| Who may do what | The CMS's own permission model, through `IContentPermissionService` | See [Authorization](CHANGE-SETS.md#authorization) |
| Side effects that need retries | `IBackgroundTaskQueue`, run by `QueuedHostedService`, with Polly | See [Retries](CHANGE-SETS.md#retries). Webhooks weren't used, as they're going away |
| Results and problems | `Attempt<TResult, TStatus>` and `PropertyValidationError`, mirrored | `AbilityResult` has a status (`AbilityOperationStatus`: Success, InvalidInput, AccessDenied, Failed) and a result. `Problem` has `ErrorMessages` and a `Variant` (the CMS's culture and segment are two of its dimensions). The CMS adapter passes Umbraco's messages straight through |

Checked, and not usable:

| CMS feature | Why not |
| --- | --- |
| Content versions, as the version a set started from | A save updates the current version in place; only publishing creates a new one. Tested against Umbraco 18: three saves left one version holding the last value. So the draft set keeps its own base values |
| Relations and tracked references, for references between entities | A relation's parent and child are node ids plus a CMS object type, so only entities with a CMS node can take part. A Commerce discount can't |
| The audit log, for non-CMS entities | Its object id is a node id. A Commerce entity can only be logged against no node, with its UDI in the comment |

Left for later:

- **Storage.** Change sets and draft sets are in memory, which doesn't work across load-balanced servers.
