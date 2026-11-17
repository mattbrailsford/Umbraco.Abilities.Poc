# Umbraco.Abilities.Poc

A proof of concept, not a product. It tests one claim: if packages declare **abilities** (operations) and **entity
adapters** (what their entities look like, and how to load and save them), and Write calls are recorded into **change sets**, then "create or
edit an entity from a description" needs no per-product code.

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) is the design this code tests.
- [README.md](README.md) is the entry point: why it exists, the idea, what it shows, what it does not prove.
- [docs/FINDINGS.md](docs/FINDINGS.md) holds the findings: what building it taught us, real Umbraco, a real model.
- [docs/WRITING-AN-ADAPTER.md](docs/WRITING-AN-ADAPTER.md), [docs/CHANGE-SETS.md](docs/CHANGE-SETS.md) and
  [docs/TESTS.md](docs/TESTS.md) hold the details of each part, and which test shows which claim.
- [REVIEWING.md](REVIEWING.md) is the guided tour: the map, how one call flows, and a reading order.

It started in the [Umbraco.AI repo](https://github.com/umbraco/Umbraco.AI) and moved here because it is not AI
specific. This repo is now the source of truth for the proof of concept and its design. The Umbraco.AI code it builds
on (the content tools and block path engine) still lives there.

Read the relevant ARCHITECTURE and docs sections before changing a design layer. Many choices there were made on purpose
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
- `Umbraco.Abilities.Commerce.Tests` (NUnit) adds real Umbraco Commerce, in its own project: Commerce installs itself
  on any Umbraco host that can see it, the CMS tests' test server included. Add `--blame-hang-timeout 2m` when
  changing its set-up: a SQLite deadlock hangs rather than fails.

## Layout

| Project | Role |
| --- | --- |
| `src/Umbraco.Abilities` | The framework. `Abilities/` (layer 1), `Entities/` (layer 2, typed layer in `Entities/Models/`), `ChangeSets/` (layer 3) |
| `src/Umbraco.Abilities.AI` | Wraps every ability's `AbilityTool` as an M.E.AI `AIFunction` |
| `src/Umbraco.Abilities.Samples` | In-memory stand-ins for product packages: CMS, Commerce, Automate, email, a third-party rule |
| `src/Umbraco.Abilities.Cms` | The real entity adapter for CMS documents, on `IContentEditingService`, plus the draft set adapter |
| `src/Umbraco.Abilities.Commerce` | The real entity adapters for Umbraco Commerce: discounts, and read-only stores and currencies |
| `src/Umbraco.Abilities.ManagementApi` | Two Management API endpoints: list the abilities, and call one. For HTTP callers such as MCP Dev |
| `src/Umbraco.Cms.DraftSets` | A stand-in for a Draft Sets feature in CMS core |
| `tests/Umbraco.Abilities.Tests` | A scripted model (`ScriptedAgent`) calling the AI tools with JSON |
| `tests/Umbraco.Abilities.Cms.Tests` | The same, against a real Umbraco, and the Management API endpoints over HTTP on Umbraco's test server |
| `tests/Umbraco.Abilities.Commerce.Tests` | The Commerce adapter against a real Umbraco and Umbraco Commerce |
| `tools/Umbraco.Abilities.McpProxy` | Plays the Umbraco CMS MCP Dev server: an MCP server whose tools are the abilities a site lists over the Management API |
| `demo/Umbraco.Abilities.Demo` | A small Umbraco 18 site with the Clean starter kit and real Umbraco Commerce (a seeded store, Shop page and products), to try the MCP proxy against by hand. Secrets come from environment variables, never files |
| `tools/Umbraco.Abilities.ModelRun` | Runs a real model (Claude) against the samples. Not a test: it costs money and needs `ANTHROPIC_API_KEY` |

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

- README, REVIEWING and the docs use plain, short sentences and tables. Keep that style.
- Keep the README an entry point. Details go in `docs/`.
- When building something teaches a lesson about the design, add it to "What building it taught us" in `docs/FINDINGS.md`.
- When a new test shows a claim, list it in `docs/TESTS.md`.
- When a gap is closed, update "What it does not prove" in the README and the "Not covered yet" list in `docs/FINDINGS.md`.
- When a finding changes the design, update `docs/ARCHITECTURE.md` too. It is the design's source of truth.
