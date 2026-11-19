# Umbraco.Abilities.Automate

Abilities as Umbraco Automate actions. Automate is a consumer: it states what it can do, and the framework's filters
decide which abilities it offers and in what shape. One action class serves every ability.

## Read it in this order

1. `Consumer/AutomateConsumer.cs`: who Automate is, as a consumer. Its audience, what it can do, its filters.
2. `Actions/AbilityActionLoader.cs`: one action per tool the consumer offers.
3. `Actions/AbilityAction.cs`: an ability as an Automate action: its form, and running it.
4. `AbilityActionServiceCollectionExtensions.cs`: the one call a site makes.

## Folders

| Folder | What's in it |
| --- | --- |
| `Consumer/` | The Automate consumer, and its own filter that names each field's editor |
| `Actions/` | The action, its loader, its settings form, and the step that calls the ability |
| `wwwroot/` | The fields editor: a page type's fields, drawn once the page type is picked |

Workarounds for things Automate can't do yet are marked `FUDGE`.
