using Umbraco.Abilities.Cms;
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
    .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoSetup>()
    .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoShop>()
    .AddNotificationHandler<UmbracoApplicationStartingNotification, DemoCommerce>()
    .Build();

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
