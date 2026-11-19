using System.Text.Json;

namespace Umbraco.Abilities.ManagementApi;

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
