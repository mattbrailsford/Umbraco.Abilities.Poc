# Umbraco.Abilities.Poc

A proof of concept, not a product. It tests one claim: if packages declare **abilities** (operations) and **entity
adapters** (what their entities look like, and how to load and save them), and Write calls are recorded into **change sets**, then "create or
edit an entity from a description" needs no per-product code.

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) is the design this code tests.
- [README.md](README.md) holds the findings: what was built, what it taught us, what it does not prove.
- [REVIEWING.md](REVIEWING.md) is the guided tour: the map, how one call flows, and a reading order.

It started in the [Umbraco.AI repo](https://github.com/umbraco/Umbraco.AI/tree/claude/pensive-hawking-ft8z3f)
(`experiments/abilities-poc`) and moved here because it is not AI specific. The Umbraco.AI code it builds on
(the content tools and block path engine) still lives there.

Read the relevant ARCHITECTURE and README sections before changing a design layer. Many choices there were made on purpose
(for example: one value tree, variation as named dimensions, caller-chosen keys).

## Build and test

```bash
dotnet build
dotnet test
```

- Targets .NET 10 and Umbraco 18 (`[18.0.0,19)`).
- The repo has its own `Directory.Build.props`, `Directory.Packages.props` and `NuGet.config`. Only nuget.org is used.
- Package versions are central: add a `<PackageVersion>` in `Directory.Packages.props`, and a version-less
  `<PackageReference>` in the project.
- `Umbraco.Abilities.Tests` (xunit) runs in seconds, all in memory.
- `Umbraco.Abilities.Cms.Tests` (NUnit) starts a real Umbraco 18 on SQLite through `Umbraco.Cms.Tests.Integration`.
  It is slower. Run one project with `dotnet test tests/<Project>`.

## Layout

| Project | Role |
| --- | --- |
| `src/Umbraco.Abilities` | The framework. `Abilities/` (layer 1), `Entities/` (layer 2, typed layer in `Entities/Models/`), `ChangeSets/` (layer 3) |
| `src/Umbraco.Abilities.AI` | Turns every ability into an M.E.AI `AIFunction` |
| `src/Umbraco.Abilities.Samples` | In-memory stand-ins for product packages: CMS, Commerce, Automate, email, a third-party rule |
| `src/Umbraco.Abilities.Cms` | The real entity adapter for CMS documents, on `IContentEditingService`, plus the draft set adapter |
| `src/Umbraco.Cms.DraftSets` | A stand-in for a Draft Sets feature in CMS core |
| `tests/Umbraco.Abilities.Tests` | A scripted model (`ScriptedAgent`) calling the AI tools with JSON |
| `tests/Umbraco.Abilities.Cms.Tests` | The same, against a real Umbraco |

## Dependency rules

These boundaries are part of what the PoC proves. Do not cross them.

- `Umbraco.Abilities.Samples` references the contract only. Never the AI adapter.
- `Umbraco.Cms.DraftSets` references Umbraco only. Never Abilities or AI.
- `Umbraco.Abilities` depends only on `Umbraco.Cms.Core` (for UDIs, notifications, the background queue) and `Polly.Core`.
- Product code must not need per-product logic in the framework. If a change needs it, that is a finding: write it down.

## Code conventions

- Nullable on, implicit usings on, latest C#.
- File-scoped namespaces. Related small types share one file (for example `Ability.cs`, `ChangeSet.cs`).
- Public types and members get a short `<summary>`. Write it for a person and a model: what it is, and when to use it.
- Test names are full sentences with underscores, describing the behaviour:
  `A_value_changed_in_both_the_set_and_the_backoffice_is_a_conflict_to_resolve`.
- Tests drive the system through the AI tools with JSON arguments (`_agent.OkAsync("entity_setValue", ...)`),
  the way a model would. Prefer that over calling services directly.
- Tests use caller-chosen keys (`Guid.NewGuid()` in the test) so entities in one set can refer to each other.

## Docs

- README and REVIEWING use plain, short sentences and tables. Keep that style.
- When building something teaches a lesson about the design, add it to "What building it taught us" in the README.
- When a gap is closed, update "What it does not prove" and the "Not covered yet" list.
- When a finding changes the design, update `docs/ARCHITECTURE.md` too. Its header says it is exported from a
  shared design doc, where comments happen, so flag design changes to the user rather than treating this copy as
  the only source.
