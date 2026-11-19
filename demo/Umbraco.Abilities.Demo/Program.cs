using Umbraco.Abilities.Automate;
using Umbraco.Abilities.Cms;
using Umbraco.Abilities.Commerce;
using Umbraco.Abilities.Demo;
using Umbraco.Abilities.ManagementApi;
using Umbraco.Commerce.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddUmbracoCommerce(commerce => commerce.AddSQLite())
    .AddComposers()
    .AddAbilities()
    .AddAbilitiesManagementApi()
    .AddCommerceAbilities()
    .AddAutomateAbilities()
    .Build();

// Every ability as an Automate action, and a Create and Update action per page type. After Umbraco's builder has
// built: only then has Automate registered its actions (see FINDINGS, "What the loader fudges").
builder.Services.AddAbilityActions();

// The demo's seed data: the MCP API user and the shop, made in the background once Umbraco is up (see DemoSeeder).
builder.Services.AddScoped<DemoSetup>();
builder.Services.AddScoped<DemoShop>();
builder.Services.AddHostedService<DemoSeeder>();

WebApplication app = builder.Build();

await app.BootUmbracoAsync();

app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

await app.RunAsync();
