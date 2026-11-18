using Umbraco.Abilities.Automate;
using Umbraco.Abilities.Cms;
using Umbraco.Abilities.Commerce;
using Umbraco.Abilities.Demo;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Commerce.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddUmbracoCommerce(commerce => commerce.AddSQLite())
    .AddComposers()
    .AddAbilities()
    .AddCommerceAbilities()
    .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoSetup>()
    .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoShop>()
    .Build();

// Every ability as an Automate action, and a Create and Update action per page type. After Umbraco's builder has
// built: only then has Automate registered its actions (see FINDINGS, "What the loader fudges").
builder.Services.AddAbilityActions();

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
