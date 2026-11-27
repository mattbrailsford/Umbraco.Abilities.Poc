# Umbraco.Abilities.UI

Shared backoffice UI for abilities: editors any surface can use, whatever product the entity comes from. Static assets
only, served from `App_Plugins/UmbracoAbilitiesUI`. Laid out as Umbraco.AI's shared UI package (`Umbraco.AI.Agent.UI`)
is, but plain JavaScript with no build step, as the rest of this proof of concept's UI is.

## Read it in this order

1. `wwwroot/umbraco-package.json`: what it adds to the backoffice.
2. `wwwroot/entity-picker.js`: the entity picker.
3. `wwwroot/entity-picker-modal.js`: its dialog, which lists entities under their group, such as a discount's store.
   `entity-picker-modal.token.js` opens it.

## What's in it

| Editor | What it does |
| --- | --- |
| Entity picker (`Umb.Abilities.PropertyEditorUi.EntityPicker`) | Picks one entity of any type with an adapter, such as a Commerce discount. It lists them by calling `entity.find` through the Management API, as the signed-in user, so it offers only what they may see. Entities with a group (`EntitySummary.Group`) are listed under it, and the group shows under the picked one. A picker within another field (`withinField`, such as the store) lists only what is within the chosen one. Its value is the entity's key. Config: `entityType`, the `consumer` to call as, and `withinField` |

It needs the Management API endpoints (`Umbraco.Abilities.ManagementApi`) on the site.

## Who uses it

- `Umbraco.Abilities.Automate`: a step's form gives a key to an entity this picker, unless the entity type has an
  Umbraco picker of its own (documents and media).
