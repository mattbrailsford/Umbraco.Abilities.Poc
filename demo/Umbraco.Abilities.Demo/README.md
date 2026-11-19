# Demo site

A small Umbraco 18.3 site to try the abilities by hand, through MCP. It runs on SQLite and installs itself on first run.
It has:

- the **Clean starter kit** (`Clean` 8.0.1): a blog site whose articles are built from blocks (rich text, code,
  images, videos), and live under the Blog page
- the abilities and their Management API endpoints (`builder.AddAbilities()`)
- **Umbraco Commerce** 18.1.11 on SQLite, with no license (fine for local use). On start it makes a store ("Demo
  store", GBP), a Shop page under Home with three products (Sun hat, Beach towel, Sunglasses), and lets Editors and
  Administrators use the store. Discounts are made through the real Commerce adapter (`AddCommerceAbilities()`)
- **Umbraco Automate** 18.6.0, in Umbraco's own database (`Umbraco:Automate:UseNamedConnectionString`). Every whole
  ability is an action, and each page type has a Create and an Update action (`AddAbilityActions()` in `Program.cs`).
  To try one, add a workspace under Automation > Settings, with an API user (such as MCP) as its service account
- the demo references `Azure.Identity` only so Commerce starts on Umbraco 18.3 (see finding 25 in `docs/FINDINGS.md`)
- an **API user** for the MCP proxy, in the Editors group, with client id `umbraco-back-office-mcp`

## Run it with one script

```bash
demo/start-demo.sh
```

It builds the site and the MCP server, starts the site, waits until it answers, then opens Claude Code with the
`umbraco-abilities` MCP server connected. When you quit Claude, the site stops.

- Sign in to the backoffice at `https://localhost:44399/umbraco` as `admin@example.com` / `password1234`. This
  password is for local demos only: it is in `appsettings.Development.json`, so the site only uses it in Development.
- The MCP API user's secret is made on the first run and kept next to the database
  (`umbraco/Data/demo-mcp-client-secret`, which git ignores). It is never in the repo.
- `--reset` deletes the demo database first, so the site installs itself again. Use it the first time if you ran
  the site before this script, as that database has a different admin password and secret.
- `--site-only` starts the site without Claude, for another MCP client.
- Claude runs in an empty folder outside the repo, with only this MCP server, so it works through the tools rather
  than reading the code. Other arguments are passed to `claude`.
- The first run installs Umbraco, Commerce and the starter kit, so it takes a minute or two.
- The site's log is `umbraco/Logs/demo-site.log`.

## Run it by hand

```bash
export Demo__Mcp__ClientSecret='<a long random secret for the API user>'
dotnet run --project demo/Umbraco.Abilities.Demo
```

The site is at `https://localhost:44399`. The database is `Umbraco.Clean.sqlite.db` in
`demo/Umbraco.Abilities.Demo/umbraco/Data`; delete it to start again.

The API user is made only on the first run that has a secret. To change the secret later, delete the database, or
change it in the backoffice under Users.

### Connect an MCP client

The proxy in `tools/Umbraco.Abilities.McpProxy` plays the Umbraco CMS MCP Dev server. Build it, then add it to a
client. For Claude Code:

```bash
dotnet build tools/Umbraco.Abilities.McpProxy
claude mcp add umbraco-abilities \
  -e UMBRACO_BASE_URL=https://localhost:44399 \
  -e UMBRACO_CLIENT_ID=umbraco-back-office-mcp \
  -e UMBRACO_CLIENT_SECRET="$Demo__Mcp__ClientSecret" \
  -- dotnet tools/Umbraco.Abilities.McpProxy/bin/Debug/net10.0/Umbraco.Abilities.McpProxy.dll
```

The proxy needs the site's HTTPS development certificate to be trusted (`dotnet dev-certs https --trust`).

## Sample prompts

Try them in order: later ones use what earlier ones made. Applied pages are saved, not published, so check them in
the backoffice.

**Look around**

> What can you create and edit on this site? List the entity types, and tell me what a blog article needs.

**Build a page from blocks**

> Start a change set and create a blog article called "Summer opening hours" under the page that lists articles.
> Build its content from a rich text block and a code snippet block, then validate and apply it.

Clean's article needs an author and a date, so the model picks them.

**Edit what's there**

> Find the "Summer opening hours" article. Add a second rich text block at the end saying we're closed on bank
> holidays, and change its title to "Summer opening hours 2026". Show me what changed, then apply it.

**Review before anything is saved**

> Start a change set. Create two articles, "Beach safety tips" and "What to pack for the beach", each with one rich
> text block. Summarize the change set for me, and don't apply it yet.

Then, once you've read the summary: "Discard that change set." Nothing reaches the site.

**A Commerce discount, through the same tools**

> Start a change set and create a discount called "Summer sale": 15% off when the order has a product from the
> Summer collection and totals at least 500, with the code SUMMER27 and the article as its landing page.
> Validate and apply it, then switch it on.

**See validation catch a mistake**

> Create a discount called "Broken dates" that starts on 1 August 2026 and ends on 1 July 2026. Validate it and tell
> me what's wrong, then fix it so it runs for the whole of July, and apply it.

**Move and delete**

> Move "Summer opening hours 2026" under the Home page and apply it. Then delete it.

Deleted pages go to the recycle bin.
