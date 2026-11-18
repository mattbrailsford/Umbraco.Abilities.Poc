using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// The framework builds tool schemas with .NET's own exporter, not M.E.AI's. A model sees the schema, so moving must not
/// change it: every sample ability's schema, as a model gets it (without the UI keywords), is checked against the one
/// M.E.AI would make. Only a format the framework adds from <c>[ValueFormat]</c> may differ.
/// </summary>
public class AbilityToolSchemaTests
{
    [Fact]
    public void Every_abilitys_input_schema_is_the_one_MEAI_would_make()
    {
        var differences = SampleSite.Create().Abilities.All
            .Select(ability => ability.Descriptor)
            .Select(descriptor => (
                descriptor.Id,
                Framework: JsonNode.Parse(UiHints.Without(AbilitySchemas.InputSchema(descriptor.InputType), UiHints.FormKeywords).GetRawText()),
                MEAI: JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(descriptor.InputType, serializerOptions: AbilityJson.Options).GetRawText())))
            .Select(s => (s.Id, Framework: WithoutAddedFormats(s.Framework, s.MEAI), s.MEAI))
            .Where(s => !JsonNode.DeepEquals(s.Framework, s.MEAI))
            .Select(s => $"{s.Id}\n  framework: {s.Framework!.ToJsonString()}\n  M.E.AI:    {s.MEAI!.ToJsonString()}")
            .ToList();

        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    // The one thing the framework adds on purpose: a format from [ValueFormat], which M.E.AI doesn't read. It is removed
    // where M.E.AI's schema has none, so everything else must still match.
    private static JsonNode? WithoutAddedFormats(JsonNode? framework, JsonNode? meai)
    {
        if (framework is JsonObject ours && meai is JsonObject theirs)
        {
            if (!theirs.ContainsKey("format"))
            {
                ours.Remove("format");
            }

            foreach (var (name, child) in ours.ToList())
            {
                WithoutAddedFormats(child, theirs[name]);
            }
        }
        else if (framework is JsonArray ourItems && meai is JsonArray theirItems)
        {
            for (var i = 0; i < Math.Min(ourItems.Count, theirItems.Count); i++)
            {
                WithoutAddedFormats(ourItems[i], theirItems[i]);
            }
        }

        return framework;
    }
}
