# Demo site

A small Umbraco 18 site to try the abilities by hand, through MCP. It runs on SQLite and installs itself on first run.
On start it adds:

- the abilities and their Management API endpoints (`builder.AddAbilities()`)
- an **Article** document type: a name, a mandatory title and a summary
- an **API user** for the MCP proxy, in the Editors group, with client id `umbraco-back-office-mcp`

No password or secret is stored in the repo. You choose them when you run it.

## Run the site

```bash
export Umbraco__CMS__Unattended__UnattendedUserPassword='<an admin password, 10+ characters>'
export Demo__Mcp__ClientSecret='<a long random secret for the API user>'
dotnet run --project demo/Umbraco.Abilities.Demo
```

The site is at `https://localhost:44399`. Sign in to the backoffice at `/umbraco` as `admin@example.com` with the admin
password. The database is in `demo/Umbraco.Abilities.Demo/umbraco/Data`; delete that folder to start again.

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

Then ask for something, for example: "Start a change set, create an article called 'Summer opening hours' with a
title and a summary, validate it and apply it." Applied articles are saved, not published.

The proxy needs the site's HTTPS development certificate to be trusted (`dotnet dev-certs https --trust`).
