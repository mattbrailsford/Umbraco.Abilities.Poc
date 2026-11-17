# Demo site

A small Umbraco 18.3 site to try the abilities by hand, through MCP. It runs on SQLite and installs itself on first run.
It has:

- the **Clean starter kit** (`Clean` 8.0.1): a blog site whose articles are built from blocks (rich text, code,
  images, videos), and live under the Blog page
- the abilities and their Management API endpoints (`builder.AddAbilities()`)
- **Umbraco Commerce** 18.1.11 on SQLite, with no license (fine for local use). On start it makes a store ("Demo
  store", GBP), a Shop page under Home with three products (Sun hat, Beach towel, Sunglasses), and lets Editors and
  Administrators use the store. Discounts are made through the real Commerce adapter (`AddCommerceAbilities()`)
- the demo references `Azure.Identity` only so Commerce starts on Umbraco 18.3 (see finding 25 in `docs/FINDINGS.md`)
- an **API user** for the MCP proxy, in the Editors group, with client id `umbraco-back-office-mcp`

No password or secret is stored in the repo. You choose them when you run it.

## Run the site

```bash
export Umbraco__CMS__Unattended__UnattendedUserPassword='<an admin password, 10+ characters>'
export Demo__Mcp__ClientSecret='<a long random secret for the API user>'
dotnet run --project demo/Umbraco.Abilities.Demo
```

The site is at `https://localhost:44399`. Sign in to the backoffice at `/umbraco` as `admin@example.com` with the admin
password. The database is `Umbraco.Clean.sqlite.db` in `demo/Umbraco.Abilities.Demo/umbraco/Data`; delete it to start again.

The API user is made only on the first run that has a secret. To change the secret later, delete the database, or
change it in the backoffice under Users.

## Connect an MCP client

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

Then ask for something, for example: "Start a change set and create a blog article called 'Summer opening hours'
under the page that lists articles. Build its content from a rich text block and a code snippet block, then validate
and apply it." Clean's article needs an author and a date, so the model picks them. Applied articles are saved, not
published.

Or a discount: "Start a change set and create a discount called 'Summer sale': 15% off when the order has a product
from the Summer collection and totals at least 500, with the code SUMMER27 and the article as its landing page.
Validate and apply it, then switch it on."

The proxy needs the site's HTTPS development certificate to be trusted (`dotnet dev-certs https --trust`).
