using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// Consumers (an AI chat, an MCP server, a workflow tool) each get their tools through filters: the base policy first,
/// then the policies and filters the consumer lists, then what other packages added to it. Every filter, policy and
/// consumer here is one a package would add; none needs a change in the framework.
/// </summary>
public class ConsumerTests
{
    private readonly SampleSite _site = SampleSite.Create();

    private const ConsumerCapabilities Chat = ConsumerCapabilities.Conversation | ConsumerCapabilities.AsksConsent;

    [Fact]
    public void A_consumer_that_can_ask_and_keeps_a_conversation_offers_every_ability()
    {
        AddConsumer("chat", Chat);

        Assert.Equal(_site.Abilities.All.Count, _site.Tools.GetTools("chat").Count);
    }

    [Fact]
    public void A_package_adds_a_filter_and_a_consumer_applies_it_without_any_framework_change()
    {
        _site.Filters.Add(new DropWhere("hide-email", t => t.Id.StartsWith("email.", StringComparison.Ordinal), "Not for this consumer."));
        AddConsumer("chat", Chat, "hide-email");

        var ids = _site.Tools.GetTools("chat").Select(t => t.Id).ToList();
        Assert.DoesNotContain("email.send", ids);
        Assert.Contains("entity.setValue", ids);
    }

    [Fact]
    public void A_package_adds_a_consumer_and_gets_a_list_from_one_policy()
    {
        _site.Filters
            .Add(new DropWhere("hide-explain", t => t.Descriptor.Kind == AbilityKind.Explain, "Nobody reads the explanation."))
            .Add(new DropWhere("hide-building-blocks", t => t.Descriptor.Granular, "A person builds from a form, not from paths."));
        _site.Policies.Add("for-forms", "hide-explain", "hide-building-blocks");
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, "for-forms");

        var tools = _site.Tools.GetTools("workflows");
        Assert.DoesNotContain(tools, t => t.Descriptor.Granular || t.Descriptor.Kind == AbilityKind.Explain);
        Assert.Contains(tools, t => t.Id == "email.send");
    }

    [Fact]
    public void Filters_run_in_the_order_the_consumer_lists_them()
    {
        _site.Filters.Add(new NoteEach("first")).Add(new NoteEach("second"));
        AddConsumer("chat", Chat, "second", "first");

        var notes = Tool("chat", "entity.setValue").Notes.Select(n => n.Filter);
        Assert.Equal(["second", "first"], notes);
    }

    [Fact]
    public void A_filter_added_to_a_policy_reaches_every_consumer_that_applies_it()
    {
        _site.Policies.Add("for-models");
        AddConsumer("chat", Chat, "for-models");
        AddConsumer("mcp", Chat, "for-models");

        // Another package adds to the shared policy, once.
        _site.Filters.Add(new DropWhere("hide-email", t => t.Id == "email.send", "Not offered to models."));
        _site.Policies.AddToPolicy("for-models", "hide-email");

        Assert.All(["chat", "mcp"], consumer => Assert.DoesNotContain(_site.Tools.GetTools(consumer), t => t.Id == "email.send"));
        var note = Tool("mcp", "email.send", kept: false).Dropped!;
        Assert.Equal(("hide-email", "for-models"), (note.Filter, note.Policy));
    }

    [Fact]
    public void A_policy_that_lists_itself_is_refused_at_start_up()
    {
        _site.Policies.Add("a", "b").Add("b", "a");

        var refused = Assert.Throws<InvalidOperationException>(_site.Tools.Validate);
        Assert.Contains("a > b > a", refused.Message);
    }

    [Fact]
    public void A_consumer_listing_a_filter_nobody_added_is_refused_at_start_up()
    {
        AddConsumer("chat", Chat, "hide-nothing");

        var refused = Assert.Throws<InvalidOperationException>(_site.Tools.Validate);
        Assert.Contains("'hide-nothing' is neither a filter nor a policy", refused.Message);
    }

    [Fact]
    public void A_package_adds_a_filter_to_another_packages_consumer_and_its_note_names_the_package()
    {
        _site.Filters.Add(new NoteEach("owners-own")).Add(new DropWhere("hide-email", t => t.Id == "email.send", "Email is sent by our own step."));
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, "owners-own");

        // Another package, which doesn't own the consumer.
        _site.Consumers.AddFilter("workflows", "hide-email");

        var dropped = Tool("workflows", "email.send", kept: false);
        Assert.Equal(["owners-own", "hide-email"], dropped.Notes.Select(n => n.Filter));
        Assert.Equal("Umbraco.Abilities.Tests", dropped.Dropped!.Package);
        Assert.Null(dropped.Dropped.Policy);
    }

    [Theory]
    [InlineData(ConsumerCapabilities.None, false)]
    [InlineData(ConsumerCapabilities.AsksConsent, true)]
    [InlineData(ConsumerCapabilities.StandingConsent, true)]
    public void An_ability_that_needs_approval_is_dropped_where_nobody_can_agree_to_it_whatever_the_consumer_applies(ConsumerCapabilities capabilities, bool kept)
    {
        // The consumer lists no filters: the base policy runs anyway.
        AddConsumer("consumer", capabilities);

        var send = Tool("consumer", "email.send", kept);
        if (!kept)
        {
            Assert.Equal(("consent", "Umbraco.Abilities", AbilityPolicyRegistry.Base), (send.Dropped!.Filter, send.Dropped.Package, send.Dropped.Policy));
            Assert.Contains("can't be undone", send.Dropped.Text);
        }
    }

    [Fact]
    public void The_change_set_tools_are_dropped_where_no_conversation_keeps_a_set_open()
    {
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent);
        AddConsumer("chat", Chat);

        Assert.Equal("change-set-lifecycle", Tool("workflows", "changeSet.create", kept: false).Dropped!.Filter);
        Tool("workflows", "entity.setValue");
        Tool("chat", "changeSet.create");
    }

    [Fact]
    public void Every_dropped_tool_stays_listed_with_why_and_only_kept_tools_are_offered()
    {
        AddConsumer("workflows", ConsumerCapabilities.None);

        var explained = _site.Tools.Explain("workflows");
        Assert.Equal(_site.Abilities.All.Count, explained.Count);
        Assert.All(explained.Where(t => t.IsDropped), t => Assert.False(string.IsNullOrWhiteSpace(t.Dropped!.Text)));
        Assert.Equal(explained.Where(t => !t.IsDropped).Select(t => t.Id), _site.Tools.GetTools("workflows").Select(t => t.Id));
    }

    [Fact]
    public async Task A_filter_can_put_several_tools_in_place_of_one_each_calling_its_ability_with_a_fixed_argument()
    {
        _site.Filters.Add(new SplitGetSchema());
        AddConsumer("chat", Chat, "split-get-schema");

        var tools = _site.Tools.GetTools("chat");
        Assert.DoesNotContain(tools, t => t.Id == "entity.getSchema");
        var document = Assert.Single(tools, t => t.Id == "entity.getSchema:document");
        Assert.Equal("entity_getSchema_document", document.Name);
        Assert.False(document.InputSchema.GetProperty("properties").TryGetProperty("entityType", out _));

        // The caller sends no entity type: the tool adds its own.
        var result = await _site.Runner.InvokeAsync(document, [], new AbilityToolSession(SampleAccess.Editor));
        Assert.True(result.Success, result.Message);
        Assert.Equal("document", result.ToJson().GetProperty("result").GetProperty("entityType").GetString());
    }

    private void AddConsumer(string alias, ConsumerCapabilities capabilities, params string[] filters)
        => _site.Consumers.Add(new AbilityConsumer(alias, alias, $"The {alias} consumer.", AbilityAudiences.Model, capabilities, filters));

    // The tool for an ability in a consumer's list, kept or dropped as expected.
    private AbilityTool Tool(string consumer, string id, bool kept = true)
    {
        var tool = Assert.Single(_site.Tools.Explain(consumer), t => t.Id == id);
        Assert.True(tool.IsDropped != kept, $"{id} was {(tool.IsDropped ? $"dropped: {tool.Dropped}" : "kept")}.");
        return tool;
    }

    private sealed class DropWhere(string alias, Func<AbilityTool, bool> when, string reason) : IAbilityFilter
    {
        public string Alias => alias;

        public string Description => reason;

        public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
        {
            yield return when(tool) ? tool.Drop(reason) : tool;
        }
    }

    private sealed class NoteEach(string alias) : IAbilityFilter
    {
        public string Alias => alias;

        public string Description => "Notes that it ran.";

        public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
        {
            yield return tool.Note("Ran.");
        }
    }

    // One getSchema per entity type, with the type fixed and taken out of the input.
    private sealed class SplitGetSchema : IAbilityFilter
    {
        public string Alias => "split-get-schema";

        public string Description => "One schema tool per entity type.";

        public IEnumerable<AbilityTool> Apply(AbilityTool tool, AbilityFilterContext context)
        {
            if (tool.Id != "entity.getSchema")
            {
                yield return tool;
                yield break;
            }

            foreach (var entityType in new[] { "document", "discount" })
            {
                var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
                schema["properties"]!.AsObject().Remove("entityType");
                (schema["required"] as JsonArray)?.Remove((schema["required"] as JsonArray)!.FirstOrDefault(r => r?.GetValue<string>() == "entityType"));
                yield return tool.Note($"For {entityType} only.") with
                {
                    Id = $"{tool.Id}:{entityType}",
                    InputSchema = JsonSerializer.SerializeToElement(schema),
                    FixedArguments = new Dictionary<string, JsonElement> { ["entityType"] = JsonSerializer.SerializeToElement(entityType) },
                };
            }
        }
    }
}
