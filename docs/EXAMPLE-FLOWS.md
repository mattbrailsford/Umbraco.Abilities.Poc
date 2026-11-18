# Example flows

Four requests, built with the same generic tools:

1. [A CMS page with a Block Grid](#flow-1-a-cms-page-with-a-block-grid)
2. [An Automate automation with different kinds of action](#flow-2-an-automate-automation)
3. [A Commerce discount with nested rules](#flow-3-a-commerce-discount)
4. [An Automate automation that branches with an If](#flow-4-an-automate-automation-that-branches)

No tool here is specific to CMS, Automate or Commerce. Each product only describes its entities (an entity adapter),
and the tools work from that.

**Where these come from.** Every call and result below was recorded from the sample site on 16-11-2026, through the
same AI tools a model gets. The calls were scripted, not chosen by a model. Results are trimmed (`…`) but not edited.
Keys like `c0000000-…-000000000001` were chosen by the caller to make them easy to read. Change set ids are random.
The products are in-memory stand-ins (see [What is here](../README.md#what-is-here)); a real CMS adapter exists and is
tested separately.

## The tools

| Tool | What it does |
| --- | --- |
| `changeSet_create` | Starts a change set. While it's the only one open, calls use it without naming it |
| `entity_getSchema` | What an entity type looks like. Ask again with `itemTypes` for the fields of the item types you'll use |
| `entity_find` | Turns a name into a key. Inside a change set it sees the set's changes |
| `entity_create` | Creates an entity in the current change set |
| `entity_addItem` | Adds an item (a block, a rule, a step) to an items field at any path |
| `entity_setValue` | Sets the value at any path |
| `entity_removeItem`, `entity_moveItem` | Removes or moves an item (not used below) |
| `entity_delete`, `entity_move` | Deletes an entity, or moves it to another parent, for types whose schema lists `delete` or `move` under `capabilities` (not used below) |
| `entity_get` | Reads an entity. Inside a change set it includes the set's changes |
| `changeSet_validate` | Checks everything in the set. Problems come back by path |
| `changeSet_summarize` | What applying the set would do, for a person to review |
| `changeSet_apply` | Saves it all. Needs a person's approval |

Products can add their own abilities too. Two appear below: `commerce.activateDiscount` (a Write) and
`commerce.listDiscounts` (a Read).

## How every call looks

**Paths.** A path goes down through values. In an object, a step is a field name. In a list of items, a step is the
item's key, never its position:

```
grid/{twoColumnKey}/right/{richTextKey}/text
rules/{groupKey}/children
steps/{stepKey}/next
```

A problem from `changeSet_validate` uses the same path an edit takes, so a fix needs no translation.

**Which change set.** Every tool except `changeSet_create` and `entity_getSchema` also takes an optional `changeSetId`. Leave it out and
the call works in the only change set open in the conversation. With two or more open, a call must name one, or it
is refused with a list of the open sets. Each flow below has one set open, so none of the calls needed it.

**Results.** Every result has the same shape, and says which change set the call used:

```jsonc
{
  "success": true,
  "status": "Success",        // or InvalidInput, AccessDenied, Failed
  "result": { … },            // what the tool returns
  "changeSet": { "id": "a72d2bb7-…", "name": "Spring landing page", "open": true }
}
```

A failure has `correctable`, `message` and `problems` (each with a `path`) in place of `result`. Below, the
`changeSet` part is left out unless it changes.

---

## Flow 1: a CMS page with a Block Grid

> "A Spring sale landing page with a full-width two-column row: the offer on the left, the end date on the right."

**What it shows:** a value that varies by culture, a Block Grid block with a layout, items inside a block's area,
and a fix made with the problem's own path.

### 1. Start a change set

```jsonc
// changeSet_create
{ "name": "Spring landing page" }

// result
{
  "success": true,
  "status": "Success",
  "result": { "changeSetId": "a72d2bb7-d5e4-461a-95ad-e1c44db2e84e", "name": "Spring landing page" },
  "changeSet": { "id": "a72d2bb7-d5e4-461a-95ad-e1c44db2e84e", "name": "Spring landing page", "open": true }
}
```

From here on, every call works in this set without naming it.

### 2. Read the schema

The first call gives the outline: the fields, and which item types each items field takes. It is plain JSON Schema
plus a few notes: `x-variations` (varies by culture), `x-item-types` (keyed, typed items) and `x-edit` (how to edit).

```jsonc
// entity_getSchema
{ "entityType": "document" }

// result (trimmed)
{
  "entityType": "document",
  "name": "Page",
  "dimensions": [ { "name": "culture", "values": ["en-US", "da-DK"] } ],
  "fields": {
    "type": "object",
    "properties": {
      "title": { "type": "string", "description": "Page name.", "x-variations": ["culture"] },
      "urlSegment": { "type": "string", "description": "URL segment, lower case with hyphens.", "x-variations": ["culture"] },
      "grid": {
        "type": "array",
        "description": "A layout grid.",
        "x-edit": "Items: each has a key and a type. Add, remove and move them with entity.addItem, removeItem and moveItem at this field's path; set their fields at '<this path>/{key}/<field>'.",
        "x-item-types": [
          { "alias": "twoColumn", "name": "Two columns", "description": "A row with a left and a right area." },
          { "alias": "richText", "name": "Rich text", "description": "A block of text." }
        ]
      }
      // … also tags, intro (rich text with blocks), related, blocks (a Block List), promos, image
    },
    "required": ["title"]
  }
}
```

The second call asks only for the item types it will use, which keeps the prompt small:

```jsonc
// entity_getSchema
{ "entityType": "document", "itemTypes": ["twoColumn", "richText"] }

// result: the same outline, plus (trimmed)
"itemTypes": {
  "twoColumn": {
    "name": "Two columns",
    "fields": {
      "type": "object",
      "properties": {
        "left": { "type": "array", "description": "Left area.", "x-item-types": [ { "alias": "richText", … } ], "minItems": 1, "maxItems": 2 },
        "right": { "type": "array", "description": "Right area.", "x-item-types": [ { "alias": "richText", … } ], "maxItems": 2 },
        "layout": {
          "type": "object",
          "properties": {
            "columnSpan": { "type": "integer", "description": "Columns the block spans, 1 to 12." },
            "rowSpan": { "type": "integer", "description": "Rows the block spans." }
          },
          "required": ["columnSpan"]
        }
      },
      "required": ["layout"]
    }
  },
  "richText": {
    "name": "Rich text",
    "fields": {
      "type": "object",
      "properties": {
        "text": { "type": "string", "description": "The text.", "x-variations": ["culture"] },
        "layout": { … }   // the same layout object
      },
      "required": ["text", "layout"]
    }
  }
}
```

A grid block's layout is an ordinary object field, and an area is an ordinary items field inside the block.

### 3. Create the page

```jsonc
// entity_create
{
  "entityType": "document",
  "key": "c0000000-0000-4000-8000-000000000001",
  "variant": { "culture": "en-US" },
  "fields": { "title": "Spring sale", "urlSegment": "spring-sale" }
}

// result
{
  "entityType": "document",
  "key": "c0000000-0000-4000-8000-000000000001",
  "name": "Spring sale",
  "editUrl": "/umbraco/section/content/workspace/document/edit/c0000000-0000-4000-8000-000000000001"
}
```

`title` and `urlSegment` vary by culture, so they are written for `en-US`.

### 4. Add the row, then a block into each area

```jsonc
// entity_addItem: the row, at the grid's root
{
  "entityType": "document",
  "key": "c0000000-0000-4000-8000-000000000001",
  "fieldPath": "grid",
  "itemType": "twoColumn",
  "itemKey": "c0000000-0000-4000-8000-000000000002",
  "fields": { "layout": { "columnSpan": 12, "rowSpan": 1 } }
}

// result
{ "itemKey": "c0000000-0000-4000-8000-000000000002", "path": "grid/c0000000-0000-4000-8000-000000000002" }
```

```jsonc
// entity_addItem: text in the left area
{
  "entityType": "document",
  "key": "c0000000-0000-4000-8000-000000000001",
  "fieldPath": "grid/c0000000-0000-4000-8000-000000000002/left",
  "itemType": "richText",
  "itemKey": "c0000000-0000-4000-8000-000000000003",
  "variant": { "culture": "en-US" },
  "fields": { "layout": { "columnSpan": 6, "rowSpan": 1 }, "text": "Everything for the garden, 20% off." }
}

// result
{ "itemKey": "c0000000-0000-4000-8000-000000000003", "path": "grid/c0000000-0000-4000-8000-000000000002/left/c0000000-0000-4000-8000-000000000003" }
```

```jsonc
// entity_addItem: a block in the right area, with its text forgotten
{
  "entityType": "document",
  "key": "c0000000-0000-4000-8000-000000000001",
  "fieldPath": "grid/c0000000-0000-4000-8000-000000000002/right",
  "itemType": "richText",
  "itemKey": "c0000000-0000-4000-8000-000000000004",
  "variant": { "culture": "en-US" },
  "fields": { "layout": { "columnSpan": 6, "rowSpan": 1 } }
}

// result
{ "itemKey": "c0000000-0000-4000-8000-000000000004", "path": "grid/c0000000-0000-4000-8000-000000000002/right/c0000000-0000-4000-8000-000000000004" }
```

The same `entity_addItem` adds a block at the root or inside an area. Only the `fieldPath` changes.

### 5. Validate, and fix by path

```jsonc
// changeSet_validate
{}

// result
{
  "valid": false,
  "entities": [
    {
      "entity": { "entityType": "document", "key": "c0000000-0000-4000-8000-000000000001", "name": "Spring sale", … },
      "problems": [
        {
          "path": "grid/c0000000-0000-4000-8000-000000000002/right/c0000000-0000-4000-8000-000000000004/text",
          "errorMessages": ["Required."],
          "variant": { "culture": "en-US" }
        }
      ]
    }
  ]
}
```

The problem says where (the path) and which culture. The fix uses that path as it is:

```jsonc
// entity_setValue
{
  "entityType": "document",
  "key": "c0000000-0000-4000-8000-000000000001",
  "path": "grid/c0000000-0000-4000-8000-000000000002/right/c0000000-0000-4000-8000-000000000004/text",
  "variant": { "culture": "en-US" },
  "value": "Ends Sunday."
}

// result
{ "path": "grid/c0000000-0000-4000-8000-000000000002/right/c0000000-0000-4000-8000-000000000004/text" }
```

A value deep in a block's layout is set the same way. Here the row is set too wide, validated, and put back:

```jsonc
// entity_setValue
{ "entityType": "document", "key": "c0000000-0000-4000-8000-000000000001", "path": "grid/c0000000-0000-4000-8000-000000000002/layout/columnSpan", "value": 14 }

// changeSet_validate → result
"problems": [
  { "path": "grid/c0000000-0000-4000-8000-000000000002/layout/columnSpan", "errorMessages": ["The field columnSpan must be between 1 and 12."], "variant": null }
]

// entity_setValue
{ "entityType": "document", "key": "c0000000-0000-4000-8000-000000000001", "path": "grid/c0000000-0000-4000-8000-000000000002/layout/columnSpan", "value": 12 }

// changeSet_validate → result
{ "valid": true, "entities": [] }
```

### 6. Read it back

The page is one value tree. Items carry their `key` and `type`; values that vary hold one value per culture.

```jsonc
// entity_get
{ "entityType": "document", "key": "c0000000-0000-4000-8000-000000000001" }

// result
{
  "entityType": "document",
  "key": "c0000000-0000-4000-8000-000000000001",
  "subtype": null,
  "version": null,
  "fields": {
    "title": { "en-US": "Spring sale" },
    "urlSegment": { "en-US": "spring-sale" },
    "grid": [
      {
        "key": "c0000000-0000-4000-8000-000000000002",
        "type": "twoColumn",
        "layout": { "columnSpan": 12, "rowSpan": 1 },
        "left": [
          {
            "key": "c0000000-0000-4000-8000-000000000003",
            "type": "richText",
            "layout": { "columnSpan": 6, "rowSpan": 1 },
            "text": { "en-US": "Everything for the garden, 20% off." }
          }
        ],
        "right": [
          {
            "key": "c0000000-0000-4000-8000-000000000004",
            "type": "richText",
            "layout": { "columnSpan": 6, "rowSpan": 1 },
            "text": { "en-US": "Ends Sunday." }
          }
        ]
      }
    ]
  }
}
```

### 7. Summarize for review

```jsonc
// changeSet_summarize
{}

// result
{
  "id": "a72d2bb7-d5e4-461a-95ad-e1c44db2e84e",
  "name": "Spring landing page",
  "entities": [
    { "entity": { "entityType": "document", "name": "Spring sale", … }, "change": "created", "edits": 7 }
  ],
  "afterApply": [],
  "applyPlan": [ { "name": "Content", "description": "1 entity, saved together: all or nothing.", "applied": false } ],
  "rebased": []
}
```

Nothing is live yet. A person reviews this, then applies the set (see the end of flow 3).

---

## Flow 2: an Automate automation

> "When an order is created, move it to Processing, email the warehouse, then switch on the Repeat customer
> discount."

**What it shows:** steps of three kinds from three packages, bindings to the trigger's data, steps linked as a graph,
a reference to an entity in another product, and a link to a step that doesn't exist yet.

Before this flow, a live discount called "Repeat customer 5%" was created with key `d0000000-…-000000000009`.

### 1. Start a change set and read the schema

```jsonc
// changeSet_create
{ "name": "New order handling" }

// result
{ "changeSetId": "6d59f564-e8db-4eb9-99ca-af52a121142b", "name": "New order handling" }
```

```jsonc
// entity_getSchema
{ "entityType": "umbraco-automate-workflow" }

// result (trimmed): name, enabled, a trigger, and the steps
"steps": {
  "type": "array",
  "description": "The steps. Each step names the step to run next, once per exit.",
  "x-item-types": [
    { "alias": "email.send", "group": "automate.step", "name": "Send email", "description": "Sends an email." },
    { "alias": "commerce.activateDiscount", "group": "automate.step", "name": "Activate discount", "description": "Switches a discount on so customers get it." },
    { "alias": "umbracoAutomate.getContent", "group": "automate.step", "name": "Get Content", "description": "Fetches a published content item and exposes its properties for use in later steps." },
    { "alias": "umbracoCommerce.changeOrderStatus", "group": "automate.step", "name": "Change Order Status", "description": "Changes the status of a Commerce order." },
    { "alias": "umbracoAutomate.if", "group": "automate.step", "name": "If", "description": "Evaluates conditions and routes to the true or false branch." }
  ],
  "minItems": 1
}
```

The step types come from four places, and none of them wrote code for this:

| Step type | Comes from |
| --- | --- |
| `umbracoCommerce.changeOrderStatus` | An action in Commerce's Automate package, read from Automate's action collection |
| `umbracoAutomate.getContent` | One of Automate's own actions, from the same collection |
| `umbracoAutomate.if` | One of Automate's control flows, read from its control flow collection |
| `email.send` | An ability in an email package |
| `commerce.activateDiscount` | An ability in Commerce |

This flow uses three of them. [Flow 4](#flow-4-an-automate-automation-that-branches) uses the If.

The trigger `umbracoCommerce.orderCreated` comes from Commerce's Automate package too. Commerce's trigger and action
need the Commerce section, so a user without it isn't offered them.

```jsonc
// entity_getSchema
{
  "entityType": "umbraco-automate-workflow",
  "itemTypes": ["umbracoCommerce.orderCreated", "umbracoCommerce.changeOrderStatus", "email.send", "commerce.activateDiscount"]
}

// result: the outline, plus (trimmed)
"itemTypes": {
  "umbracoCommerce.orderCreated": {
    "name": "Order Created",
    "fields": { "properties": { "storeId": { "type": "string", "description": "Store Only fire for this store. Leave blank to match all stores. Edited with 'Uc.PropertyEditorUi.StorePicker' in the backoffice." } }, "required": [] }
  },
  "umbracoCommerce.changeOrderStatus": {
    "name": "Change Order Status",
    "fields": {
      "properties": {
        "orderId": { "type": "string", "description": "Order ID The order to update. Can be a binding to an earlier step's output, e.g. ${trigger.orderId}.", "x-bindings": true },
        "orderStatusId": { "type": "string", "description": "Status ID The new order status GUID. Can be a binding …", "x-bindings": true },
        "next": { "type": "string", "format": "uuid", "x-references-item-in": "steps", "description": "The step to run after this one. Omit for the last step." }
      },
      "required": ["orderId", "orderStatusId"]
    }
  },
  "email.send": {
    "name": "Send email",
    "fields": {
      "properties": {
        "to": { "type": "string", "description": "Recipient email address." },
        "subject": { "type": "string", "description": "Subject line." },
        "body": { "type": "string", "description": "Optional body text." },
        "next": { … }
      },
      "required": ["to", "subject"]
    }
  },
  "commerce.activateDiscount": {
    "name": "Activate discount",
    "fields": {
      "properties": {
        "discount": { "type": "string", "format": "uuid", "x-references": "umbraco-commerce-discount", "description": "The discount to switch on." },
        "next": { … }
      },
      "required": ["discount"]
    }
  }
}
```

`x-references` says `discount` holds another entity's key. `x-references-item-in` says `next` holds the key of
another step in the same workflow.

### 2. Find the discount by name

```jsonc
// entity_find
{ "entityType": "umbraco-commerce-discount", "query": "repeat customer" }

// result
[
  {
    "entityType": "umbraco-commerce-discount",
    "key": "d0000000-0000-4000-8000-000000000009",
    "name": "Repeat customer 5%",
    "editUrl": "/umbraco/section/commerce/discount/d0000000-0000-4000-8000-000000000009"
  }
]
```

### 3. Create the workflow and its trigger

```jsonc
// entity_create
{ "entityType": "umbraco-automate-workflow", "key": "a0000000-0000-4000-8000-000000000001", "fields": { "name": "New order handling", "enabled": true } }

// result
{ "entityType": "umbraco-automate-workflow", "key": "a0000000-0000-4000-8000-000000000001", "name": "New order handling", "editUrl": "/umbraco/section/automate/workflow/a0000000-0000-4000-8000-000000000001" }
```

```jsonc
// entity_addItem
{
  "entityType": "umbraco-automate-workflow",
  "key": "a0000000-0000-4000-8000-000000000001",
  "fieldPath": "trigger",
  "itemType": "umbracoCommerce.orderCreated",
  "itemKey": "a0000000-0000-4000-8000-000000000002",
  "fields": {}
}

// result
{ "itemKey": "a0000000-0000-4000-8000-000000000002", "path": "trigger/a0000000-0000-4000-8000-000000000002" }
```

### 4. Add the steps, linked by `next`

Keys are chosen by the caller, so a step can point at the next one before it exists.

```jsonc
// entity_addItem: an Automate action, with a binding to the trigger's order
{
  "entityType": "umbraco-automate-workflow",
  "key": "a0000000-0000-4000-8000-000000000001",
  "fieldPath": "steps",
  "itemType": "umbracoCommerce.changeOrderStatus",
  "itemKey": "a0000000-0000-4000-8000-000000000003",
  "fields": {
    "orderId": "${trigger.orderId}",
    "orderStatusId": "5b0f2c1e-8d6a-4c3b-9f4e-2a7d1c9e0b11",
    "next": "a0000000-0000-4000-8000-000000000004"
  }
}

// result
{ "itemKey": "a0000000-0000-4000-8000-000000000003", "path": "steps/a0000000-0000-4000-8000-000000000003" }
```

```jsonc
// entity_addItem: an ability as a step
{
  "entityType": "umbraco-automate-workflow",
  "key": "a0000000-0000-4000-8000-000000000001",
  "fieldPath": "steps",
  "itemType": "email.send",
  "itemKey": "a0000000-0000-4000-8000-000000000004",
  "fields": {
    "to": "warehouse@example.com",
    "subject": "New order ${trigger.orderId}",
    "next": "a0000000-0000-4000-8000-000000000005"
  }
}

// result
{ "itemKey": "a0000000-0000-4000-8000-000000000004", "path": "steps/a0000000-0000-4000-8000-000000000004" }
```

### 5. Validate: a link to a step that isn't there yet

```jsonc
// changeSet_validate
{}

// result
{
  "valid": false,
  "entities": [
    {
      "entity": { "entityType": "umbraco-automate-workflow", "key": "a0000000-0000-4000-8000-000000000001", "name": "New order handling", … },
      "problems": [
        {
          "path": "steps/a0000000-0000-4000-8000-000000000004/next",
          "errorMessages": ["No item with key a0000000-0000-4000-8000-000000000005 in 'steps'."],
          "variant": null
        }
      ]
    }
  ]
}
```

Adding the step it points at fixes it. The step refers to the discount found in step 2, in another product:

```jsonc
// entity_addItem
{
  "entityType": "umbraco-automate-workflow",
  "key": "a0000000-0000-4000-8000-000000000001",
  "fieldPath": "steps",
  "itemType": "commerce.activateDiscount",
  "itemKey": "a0000000-0000-4000-8000-000000000005",
  "fields": { "discount": "d0000000-0000-4000-8000-000000000009" }
}

// result
{ "itemKey": "a0000000-0000-4000-8000-000000000005", "path": "steps/a0000000-0000-4000-8000-000000000005" }

// changeSet_validate → result
{ "valid": true, "entities": [] }
```

### 6. Summarize for review

```jsonc
// changeSet_summarize
{}

// result
{
  "id": "6d59f564-e8db-4eb9-99ca-af52a121142b",
  "name": "New order handling",
  "entities": [
    { "entity": { "entityType": "umbraco-automate-workflow", "name": "New order handling", … }, "change": "created", "edits": 5 }
  ],
  "afterApply": [],
  "applyPlan": [ { "name": "Automate", "description": "1 entity, saved together: all or nothing.", "applied": false } ],
  "rebased": []
}
```

The workflow is built, not run. Its email is a step, so it isn't sent now. Calling `email_send` directly inside a set
would be different: that is a side effect, and would be listed under `afterApply`, to run once the set is applied.

---

## Flow 3: a Commerce discount

> "15% off the Summer collection for VIP or Wholesale customers in June 2027, with the code SUMMER15."

**What it shows:** rule types from a plugin registry, a rule group holding rules ("VIP or Wholesale"), a range check
fixed by path, a product's own Read ability seeing the set, and applying.

### 1. Start a change set and read the schema

```jsonc
// changeSet_create
{ "name": "Summer sale" }

// result
{ "changeSetId": "5e2fc9e5-9d78-4974-9b1e-06150c74770e", "name": "Summer sale" }
```

```jsonc
// entity_getSchema
{ "entityType": "umbraco-commerce-discount" }

// result (trimmed)
"properties": {
  "name": { "type": "string", "description": "Name shown to editors." },
  "code": { "type": "string", "description": "Optional coupon code. Unique." },
  "startDate": { "type": "string", "format": "date", "description": "First day the discount applies." },
  "endDate": { "type": "string", "format": "date", "description": "Last day the discount applies." },
  "landingPage": { "type": "string", "format": "uuid", "x-references": "document", "description": "Optional page that promotes the discount." },
  "active": { "type": "boolean", "description": "Whether the discount is live. Use commerce.activateDiscount to switch it on." },
  "rules": {
    "type": "array",
    "description": "All rules must match for the discount to apply.",
    "x-item-types": [
      { "alias": "orderTotal", "name": "orderTotal", "description": "Commerce gives this provider no description, only a localization key. Describe it in plain words from its alias, 'orderTotal'." },
      { "alias": "customerGroup", "name": "Customer group", "description": "The customer belongs to a group." },
      { "alias": "productCollection", "name": "Product collection", "description": "The order contains a product from a collection." },
      { "alias": "groupDiscountRule", "name": "Group", "description": "A group of rules, all or any of which must match." },
      { "alias": "acmeDayOfWeek", "name": "Day of week (Acme)", "description": "The order is placed on certain days." }
    ],
    "minItems": 1
  },
  "rewards": { … }
}
```

The rule types are Commerce's discount rule providers, read from its provider collection. `acmeDayOfWeek` is a third
party's provider: it wrote no Abilities code, and still shows up.

```jsonc
// entity_getSchema
{ "entityType": "umbraco-commerce-discount", "itemTypes": ["groupDiscountRule", "customerGroup", "productCollection", "percentageOff"] }

// result: the outline, plus (trimmed)
"itemTypes": {
  "groupDiscountRule": {
    "name": "Group",
    "fields": {
      "properties": {
        "match": { "type": "string", "enum": ["all", "any"], "description": "Match Whether all child rules or any of them must match." },
        "children": { "type": "array", "description": "The rules in this group.", "x-item-types": [ … the same five rule types … ], "minItems": 1 }
      },
      "required": ["match"]
    }
  },
  "customerGroup": {
    "name": "Customer group",
    "fields": { "properties": { "group": { "type": "string", "enum": ["VIP", "Wholesale", "Retail"], "description": "Customer group" } }, "required": ["group"] }
  },
  "productCollection": {
    "name": "Product collection",
    "fields": { "properties": { "collection": { "type": "string", "format": "uuid", "x-references": "umbraco-commerce-collection", "description": "Collection" } }, "required": ["collection"] }
  },
  "percentageOff": {
    "name": "Percentage off",
    "fields": { "properties": { "percent": { "type": "number", "description": "Percentage" } }, "required": ["percent"] }
  }
}
```

A group rule's `children` is an items field like any other, so rules nest with the same tools.

### 2. Find the collection

```jsonc
// entity_find
{ "entityType": "umbraco-commerce-collection", "query": "summer" }

// result
[ { "entityType": "umbraco-commerce-collection", "key": "d66f61c9-4335-4805-aff8-5658604e580b", "name": "Summer collection", "editUrl": "/umbraco/section/commerce/collection/d66f61c9-4335-4805-aff8-5658604e580b" } ]
```

### 3. Create the discount

```jsonc
// entity_create
{
  "entityType": "umbraco-commerce-discount",
  "key": "d0000000-0000-4000-8000-000000000001",
  "fields": { "name": "Summer 15%", "code": "SUMMER15", "startDate": "2027-06-01", "endDate": "2027-06-30" }
}

// result
{ "entityType": "umbraco-commerce-discount", "key": "d0000000-0000-4000-8000-000000000001", "name": "Summer 15%", "editUrl": "/umbraco/section/commerce/discount/d0000000-0000-4000-8000-000000000001" }
```

### 4. Add the rules: a group for "VIP or Wholesale", then the collection

```jsonc
// entity_addItem: the group
{
  "entityType": "umbraco-commerce-discount",
  "key": "d0000000-0000-4000-8000-000000000001",
  "fieldPath": "rules",
  "itemType": "groupDiscountRule",
  "itemKey": "d0000000-0000-4000-8000-000000000002",
  "fields": { "match": "any" }
}

// result
{ "itemKey": "d0000000-0000-4000-8000-000000000002", "path": "rules/d0000000-0000-4000-8000-000000000002" }
```

```jsonc
// entity_addItem: a rule inside the group (no itemKey given, so one is made)
{
  "entityType": "umbraco-commerce-discount",
  "key": "d0000000-0000-4000-8000-000000000001",
  "fieldPath": "rules/d0000000-0000-4000-8000-000000000002/children",
  "itemType": "customerGroup",
  "fields": { "group": "VIP" }
}

// result
{ "itemKey": "7a5fef1c-c181-44af-a4b3-23a4f9f344f9", "path": "rules/d0000000-0000-4000-8000-000000000002/children/7a5fef1c-c181-44af-a4b3-23a4f9f344f9" }

// the same again with "group": "Wholesale" → path "rules/d0000000-…-000000000002/children/f16fb0d1-…"
```

```jsonc
// entity_addItem: the collection rule, at the top level
{
  "entityType": "umbraco-commerce-discount",
  "key": "d0000000-0000-4000-8000-000000000001",
  "fieldPath": "rules",
  "itemType": "productCollection",
  "fields": { "collection": "d66f61c9-4335-4805-aff8-5658604e580b" }
}

// result
{ "itemKey": "0206021f-fac2-45dc-b16c-a16d3735ec19", "path": "rules/0206021f-fac2-45dc-b16c-a16d3735ec19" }
```

### 5. Add the reward with a mistake, then fix it by path

```jsonc
// entity_addItem
{
  "entityType": "umbraco-commerce-discount",
  "key": "d0000000-0000-4000-8000-000000000001",
  "fieldPath": "rewards",
  "itemType": "percentageOff",
  "itemKey": "d0000000-0000-4000-8000-000000000003",
  "fields": { "percent": 150 }
}

// result
{ "itemKey": "d0000000-0000-4000-8000-000000000003", "path": "rewards/d0000000-0000-4000-8000-000000000003" }
```

```jsonc
// changeSet_validate
{}

// result
{
  "valid": false,
  "entities": [
    {
      "entity": { "entityType": "umbraco-commerce-discount", "key": "d0000000-0000-4000-8000-000000000001", "name": "Summer 15%", … },
      "problems": [
        { "path": "rewards/d0000000-0000-4000-8000-000000000003/percent", "errorMessages": ["The field percent must be between 1 and 100."], "variant": null }
      ]
    }
  ]
}
```

The range comes from the provider's own settings class (`[Range(1, 100)]`). Commerce wrote nothing for it.

```jsonc
// entity_setValue
{ "entityType": "umbraco-commerce-discount", "key": "d0000000-0000-4000-8000-000000000001", "path": "rewards/d0000000-0000-4000-8000-000000000003/percent", "value": 15 }

// result
{ "path": "rewards/d0000000-0000-4000-8000-000000000003/percent" }

// changeSet_validate → result
{ "valid": true, "entities": [] }
```

### 6. A product's own Read ability sees the set

`commerce_listDiscounts` is Commerce's own tool, not a generic one. Commerce queries what's live, and reading
through the change set adds what the current set built:

```jsonc
// commerce_listDiscounts
{}

// result
{
  "success": true,
  "status": "Success",
  "result": [
    { "key": "d0000000-0000-4000-8000-000000000001", "name": "Summer 15%", "active": false, "startDate": "2027-06-01", "endDate": "2027-06-30" }
  ],
  "changeSet": { "id": "5e2fc9e5-9d78-4974-9b1e-06150c74770e", "name": "Summer sale", "open": true }
}
```

### 7. Summarize, then a person applies

```jsonc
// changeSet_summarize
{}

// result
{
  "id": "5e2fc9e5-9d78-4974-9b1e-06150c74770e",
  "name": "Summer sale",
  "entities": [
    { "entity": { "entityType": "umbraco-commerce-discount", "name": "Summer 15%", … }, "change": "created", "edits": 7 }
  ],
  "afterApply": [],
  "applyPlan": [ { "name": "Commerce", "description": "1 entity, saved together: all or nothing.", "applied": false } ],
  "rebased": []
}
```

`changeSet_apply` is the only call that changes live data, so a chat client pauses for a person's approval first.

```jsonc
// changeSet_apply
{}

// result
{
  "success": true,
  "status": "Success",
  "result": [
    { "entityType": "umbraco-commerce-discount", "key": "d0000000-0000-4000-8000-000000000001", "name": "Summer 15%", "editUrl": "/umbraco/section/commerce/discount/d0000000-0000-4000-8000-000000000001" }
  ],
  "changeSet": { "id": "5e2fc9e5-9d78-4974-9b1e-06150c74770e", "name": "Summer sale", "open": false }
}
```

`open: false` means the set is applied and gone. The conversation now has no current set, so the same Read shows
live data, and says so with `changeSet: null`:

```jsonc
// commerce_listDiscounts
{}

// result
{
  "success": true,
  "status": "Success",
  "result": [
    { "key": "d0000000-0000-4000-8000-000000000001", "name": "Summer 15%", "active": false, "startDate": "2027-06-01", "endDate": "2027-06-30" }
  ],
  "changeSet": null
}
```

The discount is saved but still switched off. Switching it on is a separate ability, `commerce.activateDiscount`.

---

## Flow 4: an Automate automation that branches

> "When an order is created, if its total is over 1,000, email sales. Otherwise, set its status to Processing."

**What it shows:** a step with two exits, a mistake in an exit's name refused on the spot, an exit that points at a step
not added yet, and how the exits are saved in Automate's own form.

**How exits work.** A step with one exit has `next`, as in flow 2. A step that names outcomes has an `outcomes` object
instead, with one exit per outcome, and no `next`. So each step has exactly one way to say where to go, and a model
can't send every result to a step it meant for only one. The outcomes come from Automate's action outcomes (on its
`action-outcomes` branch): any action can name its own, like Get Content's `success` and `notFound`.

### 1. Start a change set and read the schema

```jsonc
// changeSet_create
{ "name": "Big orders" }

// result
{ "changeSetId": "432572c0-1c63-49b3-9b90-4e4e759376d7", "name": "Big orders" }
```

```jsonc
// entity_getSchema
{ "entityType": "umbraco-automate-workflow", "itemTypes": ["umbracoAutomate.if", "umbracoAutomate.getContent"] }

// result: the outline, plus (trimmed)
"itemTypes": {
  "umbracoAutomate.if": {
    "name": "If",
    "fields": {
      "type": "object",
      "properties": {
        "conditions": {
          "type": ["object", "null"],
          "properties": { "groups": { "type": "array", "items": { … "conditions": { "type": "array", "items": {
            "properties": {
              "leftOperand": { "type": "string" },
              "operator": { "enum": ["equals", "notEquals", "contains", "notContains", "startsWith", "endsWith", "greaterThan", "lessThan", "greaterThanOrEquals", "lessThanOrEquals", "isEmpty", "isNotEmpty"] },
              "rightOperand": { "type": "string" }
            } } } } } },
          "description": "Conditions Define conditions to determine which branch to follow. Edited with 'UmbracoAutomate.PropertyEditorUi.ConditionBuilder' in the backoffice. Can be a binding to an earlier step's output, e.g. ${trigger.orderId}.",
          "x-bindings": true
        },
        "outcomes": {
          "type": "object",
          "properties": {
            "true": { "type": "string", "format": "uuid", "x-references-item-in": "steps", "description": "The step to run when this step finishes with 'true'. The conditions match." },
            "false": { "type": "string", "format": "uuid", "x-references-item-in": "steps", "description": "The step to run when this step finishes with 'false'. The conditions don't match." }
          },
          "required": [],
          "description": "Where to go next, by how this step finishes. Omit an outcome to end the run there."
        }
      },
      "required": ["conditions"]
    }
  },
  "umbracoAutomate.getContent": {
    "name": "Get Content",
    "fields": {
      "properties": {
        "contentKey": { "type": "string", "description": "Content Key The key of the content item to fetch. Can be a binding …", "x-bindings": true },
        "outcomes": {
          "type": "object",
          "properties": {
            "success": { "type": "string", "format": "uuid", "x-references-item-in": "steps", "description": "The step to run when this step finishes with 'success'." },
            "notFound": { "type": "string", "format": "uuid", "x-references-item-in": "steps", "description": "The step to run when this step finishes with 'notFound'." }
          },
          …
        }
      },
      "required": ["contentKey"]
    }
  }
}
```

The If's conditions are Automate's own condition set: groups joined by OR, and the conditions in a group joined by AND.
Its schema comes from the settings class, so nothing was written for it.

Get Content's outcomes have no wording of their own. Automate labels them with localization keys
(`#uaOutcomes_found`), which tell a model nothing, so only the outcome's name reaches it.

### 2. Create the workflow and its trigger

```jsonc
// entity_create
{ "entityType": "umbraco-automate-workflow", "key": "b0000000-0000-4000-8000-000000000001", "fields": { "name": "Big orders", "enabled": true } }

// result
{ "entityType": "umbraco-automate-workflow", "key": "b0000000-0000-4000-8000-000000000001", "name": "Big orders", "editUrl": "/umbraco/section/automate/workflow/b0000000-0000-4000-8000-000000000001" }
```

```jsonc
// entity_addItem
{
  "entityType": "umbraco-automate-workflow",
  "key": "b0000000-0000-4000-8000-000000000001",
  "fieldPath": "trigger",
  "itemType": "umbracoCommerce.orderCreated",
  "itemKey": "b0000000-0000-4000-8000-000000000005",
  "fields": {}
}

// result
{ "itemKey": "b0000000-0000-4000-8000-000000000005", "path": "trigger/b0000000-0000-4000-8000-000000000005" }
```

### 3. Add the If, with a wrong outcome name first

The If has two exits, so they go in `outcomes`. Here they're named `yes` and `no` by mistake:

```jsonc
// entity_addItem
{
  "entityType": "umbraco-automate-workflow",
  "key": "b0000000-0000-4000-8000-000000000001",
  "fieldPath": "steps",
  "itemType": "umbracoAutomate.if",
  "itemKey": "b0000000-0000-4000-8000-000000000002",
  "fields": {
    "conditions": { "groups": [ { "conditions": [ { "leftOperand": "${trigger.orderTotal}", "operator": "greaterThan", "rightOperand": "1000" } ] } ] },
    "outcomes": { "yes": "b0000000-0000-4000-8000-000000000003", "no": "b0000000-0000-4000-8000-000000000004" }
  }
}

// result
{
  "success": false,
  "status": "InvalidInput",
  "correctable": true,
  "message": "Some fields could not be set.",
  "problems": [
    {
      "path": "steps/b0000000-0000-4000-8000-000000000002/outcomes/yes",
      "errorMessages": ["No field 'yes' here. Fields: true, false."],
      "variant": null
    }
  ],
  "changeSet": { "id": "432572c0-1c63-49b3-9b90-4e4e759376d7", "name": "Big orders", "open": true }
}
```

It's refused before anything is recorded, and the message lists the right names. The same call with `true` and
`false` works. Both exits point at steps that don't exist yet, which is fine until validate:

```jsonc
// entity_addItem: the same, with
"outcomes": { "true": "b0000000-0000-4000-8000-000000000003", "false": "b0000000-0000-4000-8000-000000000004" }

// result
{ "itemKey": "b0000000-0000-4000-8000-000000000002", "path": "steps/b0000000-0000-4000-8000-000000000002" }
```

### 4. Add the true branch, then validate

```jsonc
// entity_addItem
{
  "entityType": "umbraco-automate-workflow",
  "key": "b0000000-0000-4000-8000-000000000001",
  "fieldPath": "steps",
  "itemType": "email.send",
  "itemKey": "b0000000-0000-4000-8000-000000000003",
  "fields": { "to": "sales@example.com", "subject": "Big order ${trigger.orderId}" }
}

// result
{ "itemKey": "b0000000-0000-4000-8000-000000000003", "path": "steps/b0000000-0000-4000-8000-000000000003" }
```

```jsonc
// changeSet_validate
{}

// result
{
  "valid": false,
  "entities": [
    {
      "entity": { "entityType": "umbraco-automate-workflow", "key": "b0000000-0000-4000-8000-000000000001", "name": "Big orders", … },
      "problems": [
        {
          "path": "steps/b0000000-0000-4000-8000-000000000002/outcomes/false",
          "errorMessages": ["No item with key b0000000-0000-4000-8000-000000000004 in 'steps'."],
          "variant": null
        }
      ]
    }
  ]
}
```

The problem names the exit by its path: the If's `false` outcome.

### 5. Add the false branch

```jsonc
// entity_addItem
{
  "entityType": "umbraco-automate-workflow",
  "key": "b0000000-0000-4000-8000-000000000001",
  "fieldPath": "steps",
  "itemType": "umbracoCommerce.changeOrderStatus",
  "itemKey": "b0000000-0000-4000-8000-000000000004",
  "fields": { "orderId": "${trigger.orderId}", "orderStatusId": "5b0f2c1e-8d6a-4c3b-9f4e-2a7d1c9e0b11" }
}

// result
{ "itemKey": "b0000000-0000-4000-8000-000000000004", "path": "steps/b0000000-0000-4000-8000-000000000004" }

// changeSet_validate → result
{ "valid": true, "entities": [] }
```

### 6. Summarize and apply

```jsonc
// changeSet_summarize → result (trimmed)
{
  "entities": [ { "entity": { "entityType": "umbraco-automate-workflow", "name": "Big orders", … }, "change": "created", "edits": 5 } ],
  "afterApply": [],
  "applyPlan": [ { "name": "Automate", "description": "1 entity, saved together: all or nothing.", "applied": false } ]
}

// changeSet_apply → result
[ { "entityType": "umbraco-automate-workflow", "key": "b0000000-0000-4000-8000-000000000001", "name": "Big orders", … } ]
```

### 7. What Automate stores

The adapter saves the workflow in Automate's form: the steps without their exits, and a list of connections, each
naming its outcome. A step with one exit gets a connection with no outcome.

```jsonc
{
  "name": "Big orders",
  "enabled": true,
  "trigger": [ { "key": "b0000000-0000-4000-8000-000000000005", "type": "umbracoCommerce.orderCreated" } ],
  "steps": [
    { "key": "b0000000-0000-4000-8000-000000000002", "type": "umbracoAutomate.if", "conditions": { … } },
    { "key": "b0000000-0000-4000-8000-000000000003", "type": "email.send", "to": "sales@example.com", "subject": "Big order ${trigger.orderId}" },
    { "key": "b0000000-0000-4000-8000-000000000004", "type": "umbracoCommerce.changeOrderStatus", "orderId": "${trigger.orderId}", "orderStatusId": "5b0f2c1e-8d6a-4c3b-9f4e-2a7d1c9e0b11" }
  ],
  "connections": [
    { "sourceStepId": "b0000000-0000-4000-8000-000000000002", "targetStepId": "b0000000-0000-4000-8000-000000000003", "outcome": "true" },
    { "sourceStepId": "b0000000-0000-4000-8000-000000000002", "targetStepId": "b0000000-0000-4000-8000-000000000004", "outcome": "false" }
  ]
}
```

Read back with `entity_get`, each connection is an exit again: the If step has
`"outcomes": { "true": "b0000000-…-000000000003", "false": "b0000000-…-000000000004" }`.

---

## What was the same in all four

| | CMS page | Automate workflow | Commerce discount | Branching workflow |
| --- | --- | --- | --- | --- |
| Find a name | — | `entity_find` the discount | `entity_find` the collection | — |
| Create | `entity_create` | `entity_create` | `entity_create` | `entity_create` |
| Add items | Grid rows, blocks in areas | Trigger, steps | Rules, rules in a group, rewards | Trigger, steps |
| Nesting | `grid/{row}/left` | — (a graph, by `next`) | `rules/{group}/children` | — (a graph, by `outcomes`) |
| A reference | — | A step to a discount; a step to a step | A rule to a collection | Two exits to two steps |
| Refused on the spot | — | — | — | An outcome the If doesn't have |
| A problem by path | Missing text (per culture), span too wide | A `next` with no step | Percent out of range | An `outcomes/false` with no step |
| Fix | `entity_setValue` at the problem's path | `entity_addItem` the missing step | `entity_setValue` at the problem's path | `entity_addItem` the missing step |
| Review | `changeSet_summarize` | `changeSet_summarize` | `changeSet_summarize` | `changeSet_summarize` |

Seven generic tools did all the building. The products supplied a schema and their plugin registries.

## What these examples don't show

- **A model choosing the calls.** These were scripted. For runs where a real model chose them, see
  [Against a real model](FINDINGS.md#against-a-real-model).
- **Real Commerce and Automate.** These are stand-ins shaped like the real code.
- **Editing an existing entity, rebasing and conflicts.** See the tests listed under
  [What the tests show](TESTS.md).
- **Every Automate connection.** A step's exits each lead to one step. Automate can also send one exit to several
  steps, put a filter on a connection, give a step with outcomes an extra "any result" exit, and build Switch's
  outcomes from its cases. None of these has a field yet.
- **Stable item keys in Commerce.** Commerce stores no ids for rules, so the keys above change once the discount is
  saved. See [What the mapping costs](WRITING-AN-ADAPTER.md#what-the-mapping-costs).
