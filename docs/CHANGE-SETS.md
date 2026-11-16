# Change sets

> Part of the [Abilities proof of concept](../README.md). How the proof of concept holds changes back from live,
> applies them, rebases them onto changes saved elsewhere, and checks who may make them. The design behind it is in
> [ARCHITECTURE.md](ARCHITECTURE.md#layer-3-change-sets).

A change set records Write calls instead of running them. Whoever made the calls (a person in the backoffice, a model
through Umbraco.AI, an MCP client, an Automate workflow) sees their own changes while the live site doesn't. A person
reviews the set, then applies it.

## Applying a set

Products can each use their own connection string (Commerce, Automate and Umbraco.AI all can), so a draft
set never assumes one transaction across products. Each entity adapter names its product's unit of
work, and applying a set runs in steps:

1. **One step per product**, in the order the set first touched them: all of that product's changes save
   in its own transaction, so they apply all or nothing.
2. **Side effects last**, one step each, once every change is applied.

A change set holds two kinds of thing, kept apart:

- **Changes** to entities, which can be reviewed, validated and saved per product. Generic edits are changes,
  and so is a product ability that changes an entity, if it says which entity it edits and how
  (`IEntityEditAbility`). `commerce.activateDiscount` does: inside a set it is recorded as a change to the
  discount and shows in its working state; run directly, it saves the discount as before.
- **Side effects**, such as `email.send`: any other Write ability. They change nothing that can be reviewed or
  undone, so they are listed separately as "after apply" and run only after every change is applied.

Progress is kept on the set after each step. If a product's step fails, it rolls back; if a side effect fails,
there is nothing to roll back. Either way earlier steps stay applied and the set is kept. Its applied products
can't be edited in it any more, the rest can be fixed, and applying again resumes at the failed step without
applying anything twice. `changeSet.summarize` lists the changes, the side effects and the plan, so a person
sees before approving which parts are all or nothing and what runs afterwards.

## Rebasing

A change set records edits by path, not copies of entities, so when someone else saves an entity the set is
editing, the set's edits are replayed onto the newer version instead of the apply being refused.

- The first time a set edits an existing entity, it remembers the version it started from. If the product keeps
  version history (`IVersionedEntityAdapter`), it remembers only the version number and loads that version from
  the history when needed. Otherwise it keeps its own snapshot.
- What someone else changed is the difference between that starting version and the live one; what the set changed
  is the difference between the starting version and the set's result (`EntityDiff`, the same for every product).
- Where they don't overlap, the set rebases automatically and `changeSet.summarize` lists what changed meanwhile.
- Where both changed the same value in the same variant, or one changed something inside an item the other removed,
  it's a conflict. `changeSet.validate` reports it with both values, apply is refused until it's resolved, and
  `changeSet.resolveConflict` keeps yours or theirs. Edits to an item someone else removed can only keep theirs.
- If both made the same change, it isn't a conflict. If the starting version is gone from the product's history
  (a version clean-up), the entity can't be rebased and validation says why.

The CMS document adapter uses the snapshot for now: whether every draft save gets a new version that stays in
the history (version clean-up removes old ones) needs checking before it can rely on the history.

## Authorization

An agent acts for a user and gets that user's permissions, no more. `AbilityContext` carries the user's key, as the CMS's
services take it. Abilities never build their own permission model: an adapter that has one implements
`IAuthorizingEntityAdapter`, and is asked whether a user may read, create or update an entity, in the cultures a
change touches. It answers with a reason when not.

| When | Who is checked |
| --- | --- |
| An edit is recorded into a change set | The user making it |
| A change set is applied | The user applying it, for every change, before validating. If any is refused, nothing is applied: the user applying may not be who made the changes, and permissions can change while a set is open |
| An entity is found | The user: results are only what they may read |
| An entity is read | The user: a refusal is an `AccessDenied` result |
| A product ability is run directly | The user, the same as inside a set |

The CMS adapter asks the CMS. The user must have the Content section, then the document permission (browse, create
or update) on the document. For a new document that's its parent, or the root. Then they need access to the cultures
the change touches. Refusals are not correctable: retrying won't help, so an agent should stop and tell the user. The
in-memory samples stand in for each product's model with `SampleAccess`, where a content editor may not touch Commerce.

## Retries

A side effect that may fail for a while declares `[Ability(..., Retries = 3)]`. Applied in a change set, it runs on
Umbraco's `IBackgroundTaskQueue` once every change is applied, so a slow mail server doesn't hold up the apply. It is
retried with Polly (`Polly.Core` 8.5.0, the version an Umbraco 18 site already resolves through NPoco): exponential
backoff with jitter. Only `Failed` results are retried, never `InvalidInput` or `AccessDenied`. The outcome is a
`ChangeSetSideEffectCompletedNotification` with the result and the number of attempts, since the set is applied by then.
A side effect without retries runs inline, as before.

Two limits:

- **The queue is in memory.** Work still queued when the site restarts is lost. Durable side effects would need stored
  work, which ties into storage above.
- **Umbraco's `QueuedHostedService` processes the queue.** A site registers it (in `AddBackgroundJobs`). The integration
  test host doesn't, so the test starts Umbraco's own processor itself.

## CMS Draft Sets as a participant

The CMS is building Draft Sets for content. It shouldn't depend on Abilities, and Abilities shouldn't keep and replay
content edits itself when the CMS has its own drafts. So a product that keeps its own drafts implements
`IDraftingEntityAdapter`. The change set then holds one of the product's drafts and hands it that product's changes:

```
Change set (Abilities)
├── Content step  → a CMS draft set  (Umbraco.Cms.DraftSets: references Umbraco only)
├── Commerce step → the change set's own recorded changes, replayed
└── Side effects  → email.send, …
```

The draft set is the source of truth for content. Changes an AI makes are a CMS draft set the backoffice can review
and preview. The change set keeps only the draft set's id and the fact that it touched an entity. Products with no
drafts of their own (the in-memory samples) work as before. Both kinds mix in one set.

What the adapter (`CmsDocumentAdapter`) does:

| Change set needs | CMS draft set provides |
| --- | --- |
| Record an edit | `StageNew` for new content (whole), `StageChangesAsync` for existing content: only the property values, per culture and segment, that the edit changed. The adapter works them out by diffing the entities before and after |
| The working state | `GetAsync`: live content with the set's changes over it, or the set's new content, in editor format |
| Rebase and conflicts | `GetRebaseAsync`: what others changed since, and the values both changed. Conflicts are per property value, not per block |
| Resolve | `ResolveAsync`: keep the draft's value or the live one |
| Apply its step | `ApplyAsync`: every document in one scope, all or nothing |
| Discard | `Discard` |

Because the draft set records per-property changes over live content, rebasing is automatic: someone else's change to
another property is simply kept.

**Found while building it**

1. **A one-language document failed validation and save** on a mandatory property in the other language.
   `ValidateUpdateAsync` checks every culture unless `ValidateContentUpdateModel.Cultures` says otherwise.
   `UpdateAsync` saves anyway but returns `PropertyValidationError` with problems in every culture. The draft set and
   the adapter now validate only the cultures the document has, and treat "saved with problems" as decided by those
   problems alone. The adapter had the same bug, hidden because every earlier test page existed in both languages.
2. **Conflicts are coarser than the generic diff.** A block property is one value, so two people editing different
   blocks in the same Block List conflict. Item-level merging needs block-level change tracking in the draft set.

## If core owned the change set

The other option is for CMS core's Draft Sets to *be* the change set. Every product already depends on CMS core, but
not on Abilities, so a core coordinator would reach all of them:

```
CMS core: Draft Sets = the change set
├── coordinator: one step per product, all or nothing per product, resumable, rebase and conflicts
├── IDraftSetParticipant, public: the seam IDraftingEntityAdapter is here
├── content participant, built by the CMS team
└── Commerce, Forms, … implement their own participant

Abilities, on top
├── schema, paths, generic edit tools, item types
├── records ability calls by staging them into a draft set
└── side effects after apply, approval
```

We chose the Abilities-owned coordinator for the PoC, because it moves faster. Moving the coordinator into core later
needs nothing in the participant contract to change, as long as core's Draft Sets is built with the points below in
mind.

What core's Draft Sets must address to make that move possible is the checklist in
[ARCHITECTURE.md](ARCHITECTURE.md#what-cores-draft-sets-must-address-to-become-the-generic-change-set). It is the ask of
the CMS team.
