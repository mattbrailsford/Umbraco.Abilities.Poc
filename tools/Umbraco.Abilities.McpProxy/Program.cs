// A stand-in for the Umbraco CMS MCP Dev server: an MCP server over stdio whose tools are the abilities an Umbraco
// site lists over the Management API. It signs in as an Umbraco API user, as MCP Dev does, with the same settings:
//
//   UMBRACO_BASE_URL       the site, e.g. https://localhost:44331
//   UMBRACO_CLIENT_ID      the API user's client id
//   UMBRACO_CLIENT_SECRET  the API user's client secret
//
// The site needs Umbraco.Abilities.ManagementApi, with builder.AddAbilities().AddAbilitiesManagementApi(). The site's
// "mcp" consumer decides which tools this lists, and their shape. Add it to an MCP client, for example:
//
//   claude mcp add umbraco-abilities -e UMBRACO_BASE_URL=... -e UMBRACO_CLIENT_ID=... -e UMBRACO_CLIENT_SECRET=... \
//     -- dotnet run --project tools/Umbraco.Abilities.McpProxy
//
// Nothing is written to stdout but MCP messages. The token is fetched once, so a long session must restart it.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using ModelContextProtocol.Server;

using Umbraco.Abilities.McpProxy;

var baseUrl = Setting("UMBRACO_BASE_URL");
var umbraco = new HttpClient { BaseAddress = new Uri(baseUrl) };
var token = await umbraco.PostAsync("/umbraco/management/api/v1/security/back-office/token", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["client_id"] = Setting("UMBRACO_CLIENT_ID"),
    ["client_secret"] = Setting("UMBRACO_CLIENT_SECRET"),
}));
if (!token.IsSuccessStatusCode)
{
    await Console.Error.WriteLineAsync($"Umbraco refused the API user's sign-in: {(int)token.StatusCode} {await token.Content.ReadAsStringAsync()}");
    return 1;
}

var accessToken = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
umbraco.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

var options = new AbilitiesMcpProxy(umbraco).CreateOptions();
await using var server = McpServer.Create(new StdioServerTransport(options), options);
await server.RunAsync();
return 0;

static string Setting(string name)
    => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Set {name}.");
