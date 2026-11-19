using Microsoft.AspNetCore.Http;

using Umbraco.Abilities.ChangeSets;

namespace Umbraco.Abilities.Cms;

/// <summary>
/// Reads the change set an HTTP request works in from the <c>Umb-Change-Set</c> header and makes it current for that
/// request, as the CMS Draft Sets proof of concept does with <c>Umb-Draft-Set</c>. This is how an HTTP caller, such as
/// the Umbraco MCP over the Management API, tags its calls. Fails closed: a header naming an unknown set is refused,
/// rather than letting the request write to live data.
/// </summary>
public sealed class ChangeSetHeaderMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IChangeSetAccessor accessor, ChangeSetService changeSets)
    {
        if (!context.Request.Headers.TryGetValue(ChangeSetAccessor.HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(header.ToString(), out var id) || changeSets.Find(id) is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"Unknown or invalid change set '{header}'.");
            return;
        }

        using (accessor.Use(id))
        {
            await next(context);
        }
    }
}
