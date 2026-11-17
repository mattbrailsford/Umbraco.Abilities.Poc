using Umbraco.Abilities.Cms;
using Umbraco.Abilities.Demo;
using Umbraco.Cms.Core.Notifications;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .AddAbilities()
    .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoSetup>()
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
