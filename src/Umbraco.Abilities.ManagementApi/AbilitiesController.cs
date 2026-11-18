using System.Text.Json;
using System.Text.Json.Nodes;

using Asp.Versioning;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Consumers;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Security;

namespace Umbraco.Abilities.ManagementApi;

/// <summary>
/// Every ability over the Management API, through generic endpoints: list a consumer's tools, call one, and resolve a
/// tool's input once some arguments are chosen. An HTTP
/// caller, such as the Umbraco CMS MCP Dev server, names the consumer it is in the <c>Umb-Consumer</c> header (set once
/// on its client; <see cref="McpConsumer"/> when it doesn't) and builds one tool per listed tool, so a new ability needs no new endpoint and no new release of the
/// caller. The API is not a consumer itself: it is the road one travels. It offers only that consumer's tools, shaped by
/// its filters, and shapes each result the same way.
/// <para>
/// Calls run as the signed-in backoffice user (for MCP Dev, its API user), with that user's permissions. A call works
/// in the change set its <c>Umb-Change-Set</c> header names, or the one its <c>changeSetId</c> names. Each call stands
/// alone: nothing is kept between requests but the change sets themselves.
/// </para>
/// <para>
/// A call that can't be undone (an ability listed with <c>requiresApproval</c>, run live rather than recorded in a change
/// set) runs only with the person's consent. Without it, the call does nothing and is a 428 whose body holds an
/// <c>approval</c>: a question to ask the person, and an id. Once they agree, the caller sends the same call again with
/// that id in <c>Umb-Approval</c>. An id approves that one call, once, by that user, and for a change set only the version
/// the person was shown, so several can wait at once and none approves anything else.
/// </para>
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("abilities")]
[ApiExplorerSettings(GroupName = "Abilities")]
public sealed class AbilitiesController(
    AbilityToolProvider tools,
    AbilityConsumerRegistry consumers,
    AbilityToolRunner runner,
    IBackOfficeSecurityAccessor security) : ManagementApiControllerBase
{
    /// <summary>The MCP server's consumer: the one an HTTP caller is when it doesn't say.</summary>
    public const string McpConsumer = "mcp";

    /// <summary>The header a caller sends with an approval's id, once the person agreed to the call it was made for.</summary>
    public const string ApprovalHeader = "Umb-Approval";

    /// <summary>
    /// The header a caller names the consumer it is in, such as <c>mcp</c>. Who calls, not what is asked for, so a
    /// header, like the change set's and the approval's: a client sets it once, and every endpoint reads it.
    /// </summary>
    public const string ConsumerHeader = "Umb-Consumer";

    /// <summary>
    /// Lists a consumer's tools, with what a tool needs: its id and tool name, a description, whether it changes anything
    /// or needs a person's approval, and the JSON schema of its input.
    /// </summary>
    [HttpGet]
    [MapToApiVersion("1.0")]
    [ProducesResponseType<IReadOnlyList<AbilityResponseModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult List(CancellationToken cancellationToken)
    {
        // The list depends on the header, so a cache must keep one list per consumer.
        Response.Headers.Append("Vary", ConsumerHeader);
        var consumer = Consumer;
        return consumers.Find(consumer) is null
            ? UnknownConsumer(consumer)
            : Ok(tools.GetTools(consumer).Select(AbilityResponseModel.From).ToList());
    }

    /// <summary>
    /// Calls one ability with its input as a JSON object. The response is the same for success and failure: whether it
    /// worked, a status, the result or the problems, and the change set the call used. A correctable failure is a 400,
    /// with problems by path the caller can fix and send again.
    /// </summary>
    [HttpPost("{id}/invoke")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType<JsonElement>(StatusCodes.Status200OK)]
    [ProducesResponseType<JsonElement>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<JsonElement>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<JsonElement>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<JsonElement>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Invoke(string id, [FromBody] JsonObject? input, CancellationToken cancellationToken)
    {
        var consumer = Consumer;
        if (consumers.Find(consumer) is null)
        {
            return UnknownConsumer(consumer);
        }

        // Only a tool the consumer offers: one its filters dropped can't be called through it either.
        if (tools.FindTool(consumer, id) is not { } tool)
        {
            return NotFound(new ProblemDetails { Title = "Unknown tool", Detail = $"The {consumer} consumer offers no tool '{id}'.", Status = StatusCodes.Status404NotFound });
        }

        // A conversation of one call, for the signed-in user. The change set comes from the call, never from earlier ones.
        var session = new AbilityToolSession(CurrentUserKey(security));
        var arguments = input ?? [];
        if (tool.ChangeSetMode == ChangeSetMode.Caller
            && !arguments.ContainsKey(AbilityTool.ChangeSetIdArgument)
            && Request.Headers.TryGetValue(ChangeSetAccessor.HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            arguments[AbilityTool.ChangeSetIdArgument] = header.ToString();
        }

        var consent = Request.Headers.TryGetValue(ApprovalHeader, out var approval) && Guid.TryParse(approval.ToString(), out var approvalId)
            ? AbilityConsent.For(approvalId)
            : AbilityConsent.None;
        var result = await runner.InvokeAsync(tool, arguments, session, consent, cancellationToken);
        return StatusCode(StatusCodeOf(result), tools.ShapeResult(consumer, tool, result.ToJson()));
    }

    /// <summary>
    /// A tool's input schema once some of its arguments are chosen, such as the page type, for a form that shows what
    /// depends on them. Each choice is as current as the site: a page type added a moment ago is in it. Shaped by the
    /// consumer's filters, as the list is.
    /// </summary>
    [HttpPost("{id}/input-schema")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType<JsonElement>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InputSchema(string id, [FromBody] JsonObject? chosen, CancellationToken cancellationToken)
    {
        var consumer = Consumer;
        if (consumers.Find(consumer) is null)
        {
            return UnknownConsumer(consumer);
        }

        return await tools.ResolveInputSchemaAsync(consumer, id, chosen ?? [], cancellationToken) is { } schema
            ? Ok(schema)
            : NotFound(new ProblemDetails { Title = "Unknown tool", Detail = $"The {consumer} consumer offers no tool '{id}'.", Status = StatusCodes.Status404NotFound });
    }

    // The consumer the caller named, or the MCP server's when it named none.
    private string Consumer
        => Request.Headers.TryGetValue(ConsumerHeader, out var named) && !string.IsNullOrWhiteSpace(named)
            ? named.ToString()
            : McpConsumer;

    private NotFoundObjectResult UnknownConsumer(string consumer)
        => NotFound(new ProblemDetails { Title = "Unknown consumer", Detail = $"No consumer '{consumer}'.", Status = StatusCodes.Status404NotFound });

    private static int StatusCodeOf(AbilityToolResult result)
        => result.Success ? StatusCodes.Status200OK : result.Status switch
        {
            AbilityOperationStatus.InvalidInput => StatusCodes.Status400BadRequest,
            AbilityOperationStatus.AccessDenied => StatusCodes.Status403Forbidden,

            // The person hasn't agreed to a call that can't be undone. Ask them, then send it again with Umb-Approval.
            AbilityOperationStatus.ApprovalRequired => StatusCodes.Status428PreconditionRequired,

            // It ran and failed, for example Umbraco refused the save. Sending the same input again won't help.
            _ => StatusCodes.Status422UnprocessableEntity,
        };
}

/// <summary>One tool, as a caller needs it to offer it.</summary>
/// <param name="Id">The tool's id, used in the invoke URL, e.g. <c>entity.create</c>.</param>
/// <param name="ToolName">The id as a tool name (letters, digits, _ and -), e.g. <c>entity_create</c>.</param>
/// <param name="Name">A short name for a person.</param>
/// <param name="Description">What it does and when to use it, for the consumer's reader.</param>
/// <param name="Kind"><c>Read</c> or <c>Write</c>.</param>
/// <param name="RequiresApproval">True when a person should approve each call: it changes something and can't be undone.</param>
/// <param name="UsesChangeSet">True when the caller chooses its change set: then it takes a <c>changeSetId</c>, or the <c>Umb-Change-Set</c> header.</param>
/// <param name="InputSchema">The JSON schema of the input, as the consumer's filters shaped it.</param>
public sealed record AbilityResponseModel(
    string Id,
    string ToolName,
    string Name,
    string Description,
    string Kind,
    bool RequiresApproval,
    bool UsesChangeSet,
    JsonElement InputSchema)
{
    internal static AbilityResponseModel From(AbilityTool tool)
        => new(
            tool.Id,
            tool.Name,
            tool.Title,
            tool.Description,
            tool.Descriptor.Kind.ToString(),
            tool.Descriptor.RequiresApproval,
            tool.ChangeSetMode == ChangeSetMode.Caller,
            tool.InputSchema);
}

/// <summary>Adds the consumer HTTP tool callers are.</summary>
public static class AbilitiesManagementApiBuilderExtensions
{
    /// <summary>
    /// The MCP server's consumer (<see cref="AbilitiesController.McpConsumer"/>): a model, in a session that keeps a
    /// change set open, whose client can ask the person before what can't be undone.
    /// </summary>
    public static IAbilityConsumer McpConsumer { get; } = new AbilityConsumer(
        AbilitiesController.McpConsumer,
        "MCP server",
        "A model through an MCP server, such as Umbraco CMS MCP Dev. Its session keeps a change set open, and its client asks the person before anything that can't be undone.",
        AbilityAudiences.Model,
        ConsumerCapabilities.Conversation | ConsumerCapabilities.AsksConsent,
        [FrameworkFilters.ForModels]);

    /// <summary>Adds the MCP server's consumer, so the abilities endpoints can serve it. Call with <c>AddAbilities()</c>.</summary>
    public static IUmbracoBuilder AddAbilitiesManagementApi(this IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton(McpConsumer);
        return builder;
    }
}
