# Umbraco.Abilities.Samples

In-memory stand-ins for product packages, so the framework can be shown and tested in seconds. Each product has two
sides, kept apart:

- **The product itself** (`Domain/`, `Plugins/`): shaped like the real one, with no Abilities code.
- **Its integration** (`Models/`, `Adapters/`, `Abilities/`): what the product would add to work with abilities.

## Read it in this order

1. `Commerce/Models/DiscountEditModel.cs`: an editing contract.
2. `Commerce/Adapters/DiscountAdapter.cs`: the mapping to Commerce's own discount.
3. `Commerce/Abilities/`: two product abilities, one that reads and one that edits.
4. `SampleSite.cs`: how it's all wired, one block per package.

## Folders

| Folder | What's in it |
| --- | --- |
| `Commerce/Domain/` | The stand-in Commerce: a discount, and rule and reward providers |
| `Commerce/Models/`, `Adapters/`, `Abilities/` | Commerce's integration |
| `Commerce/CommerceAutomate.cs` | Commerce's own Automate trigger and action, with no Abilities code |
| `Cms/Models/`, `Adapters/` | Pages with blocks, grids and cultures, and media described fluently |
| `Cms/RichText/` | A rich text value and its handler |
| `Automate/Plugins/` | The stand-in Automate: triggers, actions and control flows |
| `Automate/Models/`, `Adapters/` | A workflow whose steps are plugins and other packages' abilities |
| `Acme/` | A third party's discount rule, with no Abilities code |
| `Email/` | A side effect with retries |
| `Infrastructure/` | The in-memory database and permissions every stand-in shares |
