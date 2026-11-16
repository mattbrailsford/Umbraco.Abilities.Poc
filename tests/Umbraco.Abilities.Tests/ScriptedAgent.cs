using System.Text.Json;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.AI;
using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// Plays the part of a model: calls the AI tools by name with JSON arguments, exactly as a chat client's
/// function invocation would. One agent is one conversation, with its own current change set.
/// </summary>
public sealed class ScriptedAgent
{
    public ScriptedAgent(SampleSite site)
    {
        Tools = AbilityAITools
            .Create(site.Abilities, site.Invoker, Session)
            .ToDictionary(t => t.Name);
    }

    public IReadOnlyDictionary<string, AIFunction> Tools { get; }

    /// <summary>What the conversation keeps between calls: its user and current change set.</summary>
    public AbilityToolSession Session { get; } = new(SampleAccess.Editor);

    /// <summary>The conversation's current change set. Setting it plays a surface that opened (or closed) one.</summary>
    public Guid? ChangeSetId
    {
        get => Session.ChangeSetId;
        set => Session.ChangeSetId = value;
    }

    /// <summary>The user the agent acts for. It gets their permissions, no more.</summary>
    public Guid UserKey
    {
        get => Session.UserKey;
        set => Session.UserKey = value;
    }

    public async Task<JsonElement> CallAsync(string tool, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));

        var result = await Tools[tool].InvokeAsync(arguments);
        return (JsonElement)result!;
    }

    /// <summary>Calls a tool and asserts it succeeded. Returns its output.</summary>
    public async Task<JsonElement> OkAsync(string tool, string argumentsJson)
    {
        var result = await CallAsync(tool, argumentsJson);
        Assert.True(result.GetProperty("success").GetBoolean(), $"{tool} failed: {result}");
        return result.GetProperty("result");
    }

    /// <summary>Calls a tool and asserts it failed. Returns the failure.</summary>
    public async Task<JsonElement> FailAsync(string tool, string argumentsJson)
    {
        var result = await CallAsync(tool, argumentsJson);
        Assert.False(result.GetProperty("success").GetBoolean(), $"{tool} should have failed: {result}");
        return result;
    }

    public async Task<Guid> StartChangeSetAsync(string name)
    {
        // The new set becomes the conversation's current one: nothing here copies the id.
        var output = await OkAsync("changeSet_create", $$"""{ "name": "{{name}}" }""");
        var id = output.GetProperty("changeSetId").GetGuid();
        Assert.Equal(id, ChangeSetId);
        return id;
    }
}

public static class JsonAssert
{
    public static IReadOnlyList<string> ProblemPaths(JsonElement validateOutput)
        => validateOutput.GetProperty("entities").EnumerateArray()
            .SelectMany(e => e.GetProperty("problems").EnumerateArray())
            .Select(p => p.GetProperty("path").GetString()!)
            .ToList();
}
