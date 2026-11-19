# Umbraco.Abilities.ManagementApi

Abilities over HTTP, for callers such as an MCP server. A caller says which consumer it is in the `Umb-Consumer` header,
and gets that consumer's tools.

| File | What it is |
| --- | --- |
| `McpConsumer.cs` | Who an MCP server is, as a consumer. A caller that names no consumer is this one |
| `AbilitiesController.cs` | List a consumer's tools, call one, and resolve one's input once a choice is made |
| `AbilityResponseModel.cs` | One tool, as the list returns it |
| `AbilitiesManagementApiBuilderExtensions.cs` | The one call a site makes |
