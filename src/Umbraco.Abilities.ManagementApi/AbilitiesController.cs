using System.Text.Json;
using System.Text.Json.Nodes;

using Asp.Versioning;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

using Umbraco.Abilities.AI;
using Umbraco.Abilities.ChangeSets;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;

namespace Umbraco.Abilities.ManagementApi;

/// <summary>
/// Every ability over the Management API, through two generic endpoints: list the abilities, and call one. An HTTP
/// caller, such as the Umbraco CMS MCP Dev server, builds one tool per listed ability, so a new ability needs no new
/// endpoint and no new release of the caller.
/// <para>
/// Calls run as the signed-in backoffice user (for MCP Dev, its API user), with that user's permissions. A call works
/// in the change set its <c>Umb-Change-Set</c> header names, or the one its <c>changeSetId</c> names. Each call stands
/// alone: nothing is kept between requests but the change sets themselves.
/// </para>
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("abilities")]
[ApiExplorerSettings(GroupName = "Abilities")]
public sealed class AbilitiesController(AbilityInvoker invoker, IBackOfficeSecurityAccessor security) : ManagementApiControllerBase
{
    /// <summary>
    /// Lists every ability, with what a tool needs: its id and tool name, a description, whether it changes anything
    /// or needs a person's approval, and the JSON schema of its input.
    /// </summary>
    [HttpGet]
    [MapToApiVersion("1.0")]
    [ProducesResponseType<IReadOnlyList<AbilityResponseModel>>(StatusCodes.Status200OK)]
    public IActionResult List(CancellationToken cancellationToken)
    {
        // The tool shape doesn't depend on who calls, so any session will do.
        var session = new AbilityToolSession(Guid.Empty);
        return Ok(invoker.Abilities.All
            .OrderBy(ability => ability.Descriptor.Id, StringComparer.Ordinal)
            .Select(ability => AbilityResponseModel.From(new AbilityAIFunction(ability, invoker, session)))
            .ToList());
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
    public async Task<IActionResult> Invoke(string id, [FromBody] JsonObject? input, CancellationToken cancellationToken)
    {
        if (invoker.Abilities.Find(id) is not { } ability)
        {
            return NotFound(new ProblemDetails { Title = "Unknown ability", Detail = $"No ability '{id}'.", Status = StatusCodes.Status404NotFound });
        }

        // A conversation of one call, for the signed-in user. The change set comes from the call, never from earlier ones.
        var function = new AbilityAIFunction(ability, invoker, new AbilityToolSession(CurrentUserKey(security)));
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in input ?? [])
        {
            arguments[name] = JsonSerializer.SerializeToElement(value);
        }

        if (ability.Descriptor.UsesChangeSet
            && !arguments.ContainsKey(AbilityAIFunction.ChangeSetIdArgument)
            && Request.Headers.TryGetValue(ChangeSetAccessor.HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            arguments[AbilityAIFunction.ChangeSetIdArgument] = JsonSerializer.SerializeToElement(header.ToString());
        }

        var payload = (JsonElement)(await function.InvokeAsync(arguments, cancellationToken))!;
        return StatusCode(StatusCodeOf(payload), payload);
    }

    private static int StatusCodeOf(JsonElement payload)
    {
        if (payload.GetProperty("success").GetBoolean())
        {
            return StatusCodes.Status200OK;
        }

        return Enum.Parse<AbilityOperationStatus>(payload.GetProperty("status").GetString()!) switch
        {
            AbilityOperationStatus.InvalidInput => StatusCodes.Status400BadRequest,
            AbilityOperationStatus.AccessDenied => StatusCodes.Status403Forbidden,

            // It ran and failed, for example Umbraco refused the save. Sending the same input again won't help.
            _ => StatusCodes.Status422UnprocessableEntity,
        };
    }
}

/// <summary>One ability, as a caller needs it to offer it as a tool.</summary>
/// <param name="Id">The ability's id, used in the invoke URL, e.g. <c>entity.create</c>.</param>
/// <param name="ToolName">The id as a tool name (letters, digits, _ and -), e.g. <c>entity_create</c>.</param>
/// <param name="Name">A short name for a person.</param>
/// <param name="Description">What it does and when to use it, written for a model.</param>
/// <param name="Kind"><c>Read</c> or <c>Write</c>.</param>
/// <param name="RequiresApproval">True when a person should approve each call: it changes something and can't be undone.</param>
/// <param name="UsesChangeSet">True when it works in a change set: then it takes a <c>changeSetId</c>, or the <c>Umb-Change-Set</c> header.</param>
/// <param name="InputSchema">The JSON schema of the input, including <c>changeSetId</c> when it uses a change set.</param>
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
    internal static AbilityResponseModel From(AbilityAIFunction function)
        => new(
            function.Descriptor.Id,
            function.Name,
            function.Descriptor.Name,
            function.Description,
            function.Descriptor.Kind.ToString(),
            function.Descriptor.RequiresApproval,
            function.Descriptor.UsesChangeSet,
            function.JsonSchema);
}
