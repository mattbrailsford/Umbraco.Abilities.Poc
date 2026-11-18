using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// The framework builds tool schemas with .NET's own exporter, not M.E.AI's. A model sees the schema, so moving must not
/// change it: every sample ability's schema, as a model gets it (without the UI keywords), is checked against the one
/// M.E.AI would make.
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
                Framework: JsonNode.Parse(UiHints.ForModels(AbilitySchemas.InputSchema(descriptor.InputType)).GetRawText()),
                MEAI: JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(descriptor.InputType, serializerOptions: AbilityJson.Options).GetRawText())))
            .Where(s => !JsonNode.DeepEquals(s.Framework, s.MEAI))
            .Select(s => $"{s.Id}\n  framework: {s.Framework!.ToJsonString()}\n  M.E.AI:    {s.MEAI!.ToJsonString()}")
            .ToList();

        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }
}
