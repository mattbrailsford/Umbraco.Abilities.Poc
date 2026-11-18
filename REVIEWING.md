# Reviewing the proof of concept

> **Status:** Experiment. This guide is for reviewing the design through its code: what to read, in what order, and
> what to question. The design itself is in
> [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
> [README.md](README.md) says why it exists. [docs/FINDINGS.md](docs/FINDINGS.md) has the findings.
> [docs/EXAMPLE-FLOWS.md](docs/EXAMPLE-FLOWS.md) shows the tools in use: every call and result for a page, a discount
> and two workflows, one that branches.

## What you are reviewing

It tests one claim. If packages declare **abilities** (operations) and **entity adapters** (what their entities look
like), and write calls are recorded into **change sets**, then any surface can create and edit any product's entities
with no per-product code. Creating an entity from a description is the hardest test of that, so the tests use it most.
The same pieces serve the backoffice, the Umbraco.AI copilot, Automate and MCP.

The code is about 7,400 lines of source and 2,500 lines of tests in eight projects. The source splits into the framework
(about 4,500), the in-memory samples (about 1,600), the real CMS adapter (about 1,100), the draft sets stand-in
(about 500), the Management API endpoints (about 130) and the AI adapter (about 70). A full review takes two to three hours.
A first pass over the story and the contracts (passes 1 to 3 below) takes about 45 minutes.

```bash
dotnet test   # in-memory tests (seconds), then tests against a real Umbraco 18 on SQLite (about 30 seconds)
```

## The map

Six source projects and two test projects. The arrows show what references what.

```
Umbraco.Abilities            the framework: abilities and their tool shape, entity adapters, change sets
   ↑            ↑                          (references Umbraco.Cms.Core for UDIs, notifications, the background queue)
   │            ├── Umbraco.Abilities.ManagementApi   two endpoints: list the abilities, call one (for MCP Dev)
   │            ├── Umbraco.Abilities.Automate        every ability as an Umbraco Automate action (workarounds marked FUDGE)
   │            │
Umbraco.Abilities.AI         wraps every ability's tool as an M.E.AI AIFunction: the copilot's tools
   │            │
Umbraco.Abilities.Samples    in-memory products: CMS pages and media, Commerce, an Acme rule type, email, Automate
   │
Umbraco.Abilities.Cms  ──→   Umbraco.Cms.DraftSets
   the real CMS document       a stand-in for a Draft Sets feature in CMS core:
   adapter, which is also      references Umbraco only, never Abilities
   the draft set adapter
```

Each design layer and where it lives:

| Layer | Code |
| --- | --- |
| 1. Abilities | `Abilities/Ability.cs`: `[Ability]` with `Kind` and `Reversible`, typed `AbilityBase`, failures with categories and paths, `EntitySummary` |
| 2. Entity adapters | `Entities/`: fields with values (scalars, objects with fields, items: keyed typed objects), entity and item references, an item type registry, the working copy as one JSON value tree (`Entity`, `EntityValues`), a path editor generalised from Umbraco.AI's block path engine, and the generic entity abilities. `Entities/Models/`: the typed layer package developers write against (`EntityAdapterBase<TModel>`, model attributes, `ModelEditAbilityBase`) |
| 3. Change sets | `ChangeSets/`: a recorded unit of "any Write ability call", working state worked out in memory, apply product by product (each all or nothing) and resumable, rebasing onto changes saved elsewhere meanwhile. Generalises the CMS Draft Sets PoC (`Umbraco.Cms.DraftSets`) |

The tests: `Umbraco.Abilities.Tests` (xunit) runs the samples through the AI tools. `Umbraco.Abilities.Cms.Tests` (NUnit)
does the same against a real Umbraco.

### How one call flows

A model calls a tool. Here is everything it passes through, and where each piece lives:

```
model calls entity_setValue (JSON arguments, optionally a changeSetId)
  → AbilityAIFunction                     Umbraco.Abilities.AI: M.E.AI's arguments to JSON
  → AbilityTool                           Abilities/AbilityTool.cs: the same for every surface, JSON in, result out
      picks the change set                → the changeSetId given, or the only one open in the conversation (AbilityToolSession)
      makes it current for this call      → IChangeSetAccessor.Use: ambient, gone when the call returns
  → AbilityInvoker.InvokeAsync            ChangeSets/ChangeSetService.cs (end of file)
      no change set, or not a Write  → ability.InvokeAsync: it runs now
      a Write inside a change set    → ChangeSetService.RecordAsync: recorded, not run
  → RecordAsync
      side effect (email.send)       → kept in ChangeSet.AfterApply, runs after apply
      entity edit (IEntityEditAbility):
        working state                ← GetWorkingStateAsync (live + the set's edits, or the product's draft)
        item types                   ← ItemTypeRegistry.ResolveAsync: sources and allow lists, read once per call
        edit.ApplyAsync(document, input) → EntityEditor: a path edit on the Entity
        authorize                    → the adapter asks the product: may this user make this change?
        keep it                      → ChangeSet.Changes, or a product's own draft (IDraftingEntityAdapter)
  → result JSON back to the model: { success, status, result } or { status, correctable, message, problems },
    plus the change set the call used: { id, name, open }

later, model calls changeSet_apply
  → ChangeSetService.ApplyAsync
      authorize every change for the user applying  → AccessDenied, nothing applied
      validate (schema, then each product's rules, then conflicts with changes saved elsewhere)
      ChangeSetApplyingNotification (cancelable)
      one step per product, each in its own transaction: save each entity, or apply the product's draft
      side effects: inline, or queued on the background queue with retries
      ChangeSetAppliedNotification
```

## Reading order

Each pass builds on the one before it. The **Look for** notes are what a reviewer should push on.

### Pass 1: the story, through the tests (15 minutes)

Start with what the system does, before how it does it.

1. `tests/Umbraco.Abilities.Tests/ScriptedAgent.cs` is the harness. It plays a model: it calls the AI tools by name, with
   JSON, as a user. Every test reads like a conversation.
2. `CreationPromptTests.Discount_from_text_is_built_in_a_draft_set_and_only_goes_live_when_applied` covers "10% off the
   Summer collection for VIP customers in December". The model discovers the schema, finds the collection by name,
   builds the discount step by step and validates it. Nothing is live until it applies.
3. `CreationPromptTests.Validation_reports_problems_by_path_and_the_agent_fixes_them_with_the_same_paths` shows that
   problem paths are edit paths, so a model can fix its own work.
4. `ChangeSetTests.A_campaign_spanning_content_commerce_and_a_side_effect_is_applied_together` is one set holding a
   page, a discount that refers to that page, and an email to send afterwards.

**Look for:** whether the tool calls are ones a real model could plausibly make. They're scripted here. A real model
made the same kind of calls in a few runs, on discounts and pages, (see [Against a real model](docs/FINDINGS.md#against-a-real-model)), but that is not proof it always
will.

### Pass 2: layer 1, abilities (15 minutes)

| File | What it is |
| --- | --- |
| `src/Umbraco.Abilities/Abilities/Ability.cs` | The contract. `[Ability]` declares an operation: id, `Kind` (Explain, Read, Transform, Trigger, Write), `Reversible`, `Retries`, and `UsesChangeSet` (false for a read that can't depend on a set, like a schema). `AbilityBase<TInput, TOutput>` gives typed input from JSON. `AbilityBase<TOutput>` is for an ability that takes no input, like the change set tools. `AbilityContext` holds the acting user's key. Which change set a call works in is ambient, not context: see `ChangeSets/ChangeSetAccessor.cs`. `AbilityResult` mirrors the CMS's `Attempt<TResult, TStatus>`, and `Problem` mirrors `PropertyValidationError` |
| `src/Umbraco.Abilities/Abilities/AbilityTool.cs` | An ability as a tool, for every surface (AI, Management API, MCP). Its input schema comes from .NET's own JSON schema exporter, with descriptions from `[Description]`. It adds an optional `changeSetId` to every tool that uses a change set, keeps the sets open in the conversation in `AbilityToolSession` (with two or more open, a call must name one), and reports the set each call used in `AbilityToolResult` |
| `src/Umbraco.Abilities.AI/AbilityAIFunction.cs` | The whole AI adapter: each ability's `AbilityTool` wrapped as an `AIFunction`. Only irreversible Writes need approval |
| `AbilityInvoker` (end of `ChangeSets/ChangeSetService.cs`) | The single entry point. A Write while a change set is current is recorded instead of run |

**Look for:** whether `Kind` plus `Reversible` is enough to decide approval. Finding 1 in [FINDINGS.md](docs/FINDINGS.md#what-building-it-taught-us) says approval
depends on the call, not just the tool. Also check whether the result shape suits Automate as well as AI.

### Pass 3: layer 2, entity adapters (45 minutes)

This is the core idea: a product-neutral way to describe and edit any entity. Read in this order.

| File | What it is |
| --- | --- |
| `Entities/Schema.cs` | The schema language. `ObjectSchema` holds fields. A **field** has a value type, required, allowed values, references and the dimensions it `VariesBy`; the schema lists the dimensions (`VariationDimension`) and their values. Two value types nest: `Object` (fields of its own, such as a block's settings or a grid block's layout) and `Items` (keyed, typed objects: rules, blocks, workflow steps, with min and max). `ItemTypeRegistry` maps a group to item types, registered once or read from an `IItemTypeSource`: a product's plugin registry, or configuration such as a data type's allowed blocks (a source says which groups it serves). Sources, allow lists and availability can wait, so each call resolves the groups its entity's schema uses into `ItemTypes`, which the rest of the call reads without waiting |
| `Entities/IEntityAdapter.cs` | What a product implements: describe, new, load, validate, save, find, and optional subtypes (document types). `IVersionedEntityAdapter` is for products with version history; `IDeletableEntityAdapter` and `ITreeEntityAdapter` opt in to delete and move. The registry refuses an adapter that doesn't check access |
| `Entities/Entity.cs` | The working copy: one JSON value tree. A varying value is stored per variant where its field is. `EntityValues` reads and writes values through their fields, `Walk` lists every value, and `Find` is the one path walker: object steps by field, item steps by key |
| `Entities/EntityEditor.cs` | Path editing over `EntityValues.Find`, generalised from Umbraco.AI's block path engine: `title`, `blocks/{key}/heading`, `blocks/{key}/settings/theme`. `FieldChecker` checks and normalizes values. A UDI given for a reference is turned into the field's own form |
| `Entities/SchemaValidator.cs` | Generic validation from the schema: required (per culture), allowed values, references that exist (including in the same set), item counts. It also renders the JSON Schema the model sees |
| `Entities/EntityAbilities.cs` | The generic tools every entity gets: `entity.getSchema`, `find`, `get`, `create`, `setValue`, `addItem`, `removeItem`, `moveItem`, and `delete` and `move` for adapters that opt in |
| `Entities/EntityUdis.cs` | Identity is a UDI. Entity types are UDI entity types, registered with `UdiParser` |
| `Entities/ValueHandlers.cs` | `IValueHandler`: behaviour a value's schema can't say. Told when items inside the value are added, removed or moved, and checks rules between its parts. Paths and storage stay the framework's |
| `Entities/EntityAuthorization.cs` | `IAuthorizingEntityAdapter`: the product answers "may this user read, create or update this entity, in these cultures?" |
| `Entities/ItemTypeFields.cs` | Turns a product's setting metadata (CLR type, rules, editor alias, bindings) into fields: how plugin seams become item types with nothing written per plugin |
| `Entities/AbilityItemTypes.cs` | Abilities become item types: workflow steps can be any package's abilities |

Then the typed layer, which is what a package developer actually writes against:

| File | What it is |
| --- | --- |
| `Entities/Models/ModelAttributes.cs` | `[Entity]`, `[ItemType]`, `[Items]`, `[EntityName]`, `[EntityVersion]`, `[ValueHandler]`, `Varying<T>`, `EntityItemModel`, `DynamicItem`. A property whose type is a class becomes an object field |
| `Entities/Models/ModelConfiguration.cs` | The same, fluently, for classes a package can't annotate, such as Management API request models |
| `Entities/Models/ModelSchemas.cs` | Works out the schema from a class, maps the class to and from an `Entity`, and runs DataAnnotations on entity paths |
| `Entities/Models/EntityAdapterBase.cs` | `EntityAdapterBase<TModel>`: a package implements load and save of its model, and the framework does the rest. `ModelEditAbilityBase` turns `Edit(model, input)` into an ability that works directly and inside a change set |

**Look for:**
- whether the schema language can carry your product's entities;
- whether paths are a good contract for a model;
- whether the typed layer removes enough work;
- how much the editing contract plus its mapping costs a real package. [What the mapping costs](docs/WRITING-AN-ADAPTER.md#what-the-mapping-costs) has the numbers.

### Pass 4: the samples, as package code (20 minutes)

These are what a package would write. Judge them as such: how short, how obvious, how much is boilerplate.

| File | What it shows |
| --- | --- |
| `Samples/Commerce/Domain.cs` and `Providers.cs` | Commerce's own model, with no Abilities code: a discount shaped like Commerce's (immutable, `AsWritable()`, UTC dates, rules as provider alias plus string settings with children for groups, no rule ids), and rule and reward providers shaped like Umbraco.Commerce.Core's |
| `Samples/Commerce/Commerce.cs` | Commerce's integration: the editing contract (`DiscountEditModel`, whose rules and rewards are `DynamicItem`s), the adapter that maps to the domain, one item type source per provider seam, and the provider mapper. Also `ActivateDiscountAbility`, a product ability that edits an entity |
| `Samples/Acme/AcmeWeekdayRule.cs` | A third party's rule, written as a plain Commerce provider with no Abilities code |
| `Samples/Automate/Plugins.cs` | Automate's plugin model: triggers, actions and control flows with `[Field]` settings, required sections and outcomes |
| `Samples/Commerce/CommerceAutomate.cs` | Commerce's Automate package: a trigger and an action, one with settings that take bindings |
| `Samples/Cms/RichTextValue.cs` | A Rich Text-shaped value (markup plus blocks) and its handler: the inner model is the editor's, not the framework's |
| `Samples/Cms/CmsPage.cs` | CMS-shaped content: culture variation, blocks with settings, a grid with placement and areas, per-culture block lists, pickers. Media is described **fluently** over plain request models |
| `Samples/Automate/Workflow.cs` | A workflow as a model class whose steps are `DynamicItem`s. Step types are Automate's plugins and other packages' abilities, from item type sources. A step's outcomes become its exits; the adapter converts them to Automate's list of connections on save, and back on load |
| `Samples/Email/SendEmail.cs` | A side effect with retries |
| `Samples/SampleSite.cs` | How it's wired, as composers would wire it. Each block is one package |
| `Samples/InMemoryDatabase.cs`, `SampleAccess.cs` | Stand-ins for each product's storage, transaction and permission model |

**Look for:** how much each product writes per plugin seam (one method that describes a plugin, one line per seam in `ItemTypeSources`, one editor table), and the derived keys in `DiscountAdapter`, which exist because Commerce rules have no ids. That's a known
limit to raise with Commerce.

### Pass 5: layer 3, change sets (40 minutes)

| File | What it is |
| --- | --- |
| `ChangeSets/ChangeSet.cs` | The data. `Changes` are recorded entity edits. `AfterApply` holds side effects. `AppliedSteps` lets apply resume. `Bases` and `BaseVersions` record where rebasing starts from, and `Resolutions` records how conflicts were resolved. `Drafts` holds product drafts. Also the seams `IUnitOfWorkProvider`, `IEntityEditAbility` and `IDraftingEntityAdapter` |
| `ChangeSets/ChangeSetService.cs` | The coordinator. Read its public methods top to bottom (see below) |
| `Entities/EntityDiff.cs` | Compares two versions of any entity, by path. This is what makes rebasing generic |
| `ChangeSets/ChangeSetAbilities.cs` | The `changeSet.*` tools. They work on the current set and take no id |
| `ChangeSets/ChangeSetAccessor.cs` | The change set the current call works in, ambient like the Draft Sets accessor: per request, or per async flow outside one. Set by `ChangeSetHeaderMiddleware` (`Umbraco.Abilities.Cms`) from the `Umb-Change-Set` header, or by the AI adapter for one tool call |
| `ChangeSets/ChangeSetNotifications.cs` | CMS notifications: a cancelable applying, then applied, discarded, and side effect completed |

In `ChangeSetService.cs`, read the public methods in this order:

1. `RecordAsync`: how an edit becomes a recorded change (or a staged draft change), including the authorization check.
2. `ComputeAsync` (private, behind `GetWorkingStateAsync` and `GetRebasedAsync`): one entity's state, worked out once.
   The set's edits are replayed onto the **current** live version, which is what rebasing means here, and replayed onto
   the starting version to find where both sides changed the same value. Validate, summarize and apply all use it.
   `ReadAsync` and `ReadAllAsync` use it too: they are how any read, a product's own included, sees the current set.
3. `ResolveConflictAsync`: keeping yours or theirs for one conflict.
4. `ValidateAsync`: schema, product rules and unresolved conflicts, per entity.
5. `ApplyAsync`: authorize for the applying user, validate, the cancelable notification, then one step per product (each
   all or nothing, resumable), then side effects (inline, or queued with Polly retries).

**Look for:**
- **Per-product, not cross-product, atomicity.** Products use their own connection strings, so a set applies one product
  at a time. A failure leaves earlier products applied and resumable. Check whether that's acceptable for your product.
- **Replay determinism.** Keys are chosen when a change is recorded, so they don't change on replay.
- **Conflicts.** Check how they're detected and resolved.
- **Ordering inside `ApplyAsync`:** authorization comes before validation.

### Pass 6: against a real Umbraco (40 minutes)

| File | What it is |
| --- | --- |
| `Umbraco.Abilities.Cms/CmsSchemaMapper.cs` | Builds the schema from document and element types: a name and parent, then each property as its editor's handler describes it. Sets each field's alias and variation from the property |
| `Umbraco.Abilities.Cms/PropertyEditors/PropertyEditorHandlers.cs` | The contract a property editor implements (`IPropertyEditorEntityHandler`: describe, read, write), the contexts handlers get, error locators, and `IPropertyEditorItemTypes` for editors whose values hold items |
| `Umbraco.Abilities.Cms/PropertyEditors/BlockEditorHandler.cs` | Block List and Block Grid: one item type group per data type read from its configuration each call, values read and written with Umbraco's block value classes, and Umbraco's JSON error paths turned back into entity paths |
| `Umbraco.Abilities.Cms/PropertyEditors/PropertyEditorEntityHandlerCollection.cs` | The collection of packages' handlers, and the composer that finds every handler in the scanned assemblies |
| `Umbraco.Abilities.Cms/PropertyEditors/ValueEditorHandlers.cs` | The core editors whose value is the field's value (one line each), and the fallback: a whole JSON value with Umbraco's value schema |
| `tests/Umbraco.Abilities.Tests/DraftingTests.cs` | A fake product with drafts, twice, sharing one transaction: each keeps its own draft |
| `tests/Umbraco.Abilities.Cms.Tests/StarRatingEditor.cs` | A third party's editor and its handler, as a package would ship them |
| `Umbraco.Abilities.Cms/ContentTypeEntityAdapterBase.cs` | The base for entities whose types are content types: the schema, reading stored values into an entity and writing it back for the editing services, variants and segments, Umbraco's errors mapped to entity paths, and the values an edit touched. `CmsContentSchemas` is what all such adapters share |
| `Umbraco.Abilities.Cms/CmsMediaAdapter.cs` | Media on the base: types, load and save through `IMediaEditingService`, and Media section permissions. No drafts, so a change set replays its edits |
| `Umbraco.Abilities.Cms/CmsDocumentAdapter.cs` | Documents on the base. Loads live content in editor format, and validates through `IContentEditingService`. Content is applied through its draft set, so its `SaveAsync` is unused. It is also the **draft set adapter** (stage, read, rebase, resolve, apply), and **authorization** goes through `IContentPermissionService` |
| `Umbraco.Cms.DraftSets/DraftSetService.cs` | The stand-in for CMS core's Draft Sets. It records per-property changes over live content and reads through them, reports what others changed and the conflicts, and applies in one scope. It writes audit entries and raises notifications through the scope. It references Umbraco only |
| `tests/Umbraco.Abilities.Cms.Tests/CmsDocumentAdapterTests.cs` | Everything above against Umbraco 18 on SQLite: blocks, settings, grids and cultures saved by Umbraco; validation paths; rollback; resume; rebasing; draft sets; vetoes; real user permissions; the background queue |

**Look for:**
- **The adapter's two jobs.** Reading `CmsDocumentAdapter` alongside `DraftSetService`, ask whether the line between
  them is where core's Draft Sets would draw it.
- **The core checklist.** "[What core's Draft Sets must address](docs/ARCHITECTURE.md#what-cores-draft-sets-must-address-to-become-the-generic-change-set)" is the ask of the CMS team.
- **Validation in some cultures only.** Look at how validation is limited to the cultures a document has.
- **The property editor contract.** Read `StarRatingEditor.cs`, then `BlockEditorHandler`. Is describe, read and write
  enough for the editors you know, and should the contract live in CMS core (see
  [FINDINGS](docs/FINDINGS.md#where-property-editor-handlers-should-live))?

## How it all connects

The lifecycle of one change set, end to end:

| Stage | What happens | Where |
| --- | --- | --- |
| Discover | The model reads the schema, in two steps, and finds entities by name | `GetEntitySchemaAbility`, `FindEntityAbility`, `SchemaValidator` (schema) |
| Start | `changeSet.create` | `ChangeSetService.Create` |
| Edit | Each Write is recorded. The working state is live plus the edits (or the product's draft). Each edit is authorized for the user making it | `AbilityInvoker`, `RecordAsync`, `EntityEditor`, `IAuthorizingEntityAdapter` |
| Read inside the set | Reads show the set's changes; reads outside show live data | `GetWorkingStateAsync`, `GetEntityAbility` |
| Check | Schema, product rules and conflicts with changes saved elsewhere | `ValidateAsync`, `SchemaValidator`, `GetRebasedAsync`, `EntityDiff` |
| Resolve | Keep yours or theirs for each conflict | `ResolveConflictAsync` |
| Apply | Authorize for the applier, validate, cancelable notification, one transaction per product, side effects, applied notification | `ApplyAsync`, `IUnitOfWorkProvider`, `IDraftingEntityAdapter`, `IBackgroundTaskQueue` |
| After | A side effect with retries reports through a notification | `ChangeSetSideEffectCompletedNotification` |

The interfaces a product implements are the extension points. Everything else is the framework's:

| Interface | Implement it to |
| --- | --- |
| `IAbility` (via `AbilityBase`) | Offer an operation |
| `IEntityAdapter`, or `EntityAdapterBase<TModel>` | Make an entity type editable from text |
| `IEntityEditAbility` (via `ModelEditAbilityBase`) | Make a product ability count as a change inside a set |
| `IUnitOfWorkProvider` | Give your product's step its own transaction |
| `IVersionedEntityAdapter` | Rebase from your version history instead of a snapshot |
| `IDraftingEntityAdapter` | Keep your own drafts (CMS Draft Sets) instead of the set replaying edits |
| `IAuthorizingEntityAdapter` | Apply your own permission model |
| `IItemTypeSource` | Turn a plugin registry (providers, triggers, actions) into item types |
| `IValueHandler` | Own the model inside a value: an editor's (Rich Text) or a product's |

## What to question most

These are the judgments the PoC makes that a reviewer is best placed to challenge:

1. **The schema language.** Fields (values or items), item types, settings and placement cover discounts, workflows and CMS
   blocks here. Will they cover your entity?
2. **Paths as the contract with a model.** They make the fix loop simple (a problem path is an edit path). Are they
   natural for a model to produce?
3. **The editing contract and its mapping.** About 35 lines per entity for Commerce, with a shared helper. Is that cheap
   enough for package developers?
4. **Atomicity per product.** Is "each product all or nothing, and resumable" acceptable, given products can't share a
   transaction?
5. **Item types from registries.** One source and one mapper per plugin seam, plus an editor table. Is the metadata
   your registry already has enough, and is a field of `DynamicItem`s acceptable in a typed contract?
6. **Values as their editor's model.** The framework edits inside a value only through its handler, and keyed items
   are one built-in value type. Is that the right line between the language and the editors? Handled values rebase per
   field and their references aren't seen: acceptable as fallbacks?
7. **Who owns the coordinator.** Abilities owns it here. "[If core owned the change set](docs/CHANGE-SETS.md#if-core-owned-the-change-set)" covers the
   alternative, and what core's Draft Sets must do to make it possible.
8. **What it doesn't prove.** No LLM is in the loop. Change sets and draft sets aren't stored. Retried side effects aren't
   durable. See [What it does not prove](README.md#what-it-does-not-prove).

## Where the decisions are written down

- [README.md](README.md): why it exists, what it shows, and what it does not prove.
- [FINDINGS.md](docs/FINDINGS.md): what building it taught us, and what real Umbraco and a real model showed.
- [WRITING-AN-ADAPTER.md](docs/WRITING-AN-ADAPTER.md): what a package writes, and what it costs.
- [CHANGE-SETS.md](docs/CHANGE-SETS.md): applying, rebasing, authorization, retries and CMS Draft Sets.
- [TESTS.md](docs/TESTS.md): each claim and the tests that show it.
- [ARCHITECTURE.md](docs/ARCHITECTURE.md): the design, with the same findings
  folded in.
- [EXAMPLE-FLOWS.md](docs/EXAMPLE-FLOWS.md): four recorded flows, call by call.
- Commit messages on this branch: each records what changed and why, in order.
