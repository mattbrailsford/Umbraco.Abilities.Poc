using System.ComponentModel;
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
            .Add(new DropWhere("acme-hide-explain", t => t.Descriptor.Kind == AbilityKind.Explain, "Nobody reads the explanation."))
            .Add(new DropWhere("acme-hide-blocks", t => t.Descriptor.Granular, "A person builds from a form, not from paths."));
        _site.Policies.Add("acme-tidy", "acme-hide-explain", "acme-hide-blocks");
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, "acme-tidy");

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
        AddConsumer("chat", Chat, FrameworkFilters.ForModels);
        AddConsumer("mcp", Chat, FrameworkFilters.ForModels);

        // Another package adds to the framework's policy, once.
        _site.Filters.Add(new DropWhere("hide-email", t => t.Id == "email.send", "Not offered to models."));
        _site.Policies.AddToPolicy(FrameworkFilters.ForModels, "hide-email");

        Assert.All(["chat", "mcp"], consumer => Assert.DoesNotContain(_site.Tools.GetTools(consumer), t => t.Id == "email.send"));
        var note = Tool("mcp", "email.send", kept: false).Dropped!;
        Assert.Equal(("hide-email", FrameworkFilters.ForModels), (note.Filter, note.Policy));
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

    [Fact]
    public void An_AI_chat_offers_every_ability_as_before_with_changeSetId_where_the_caller_chooses_the_set()
    {
        _site.Consumers.Add(AI.AbilityAITools.Consumer);

        var tools = _site.Tools.GetTools(AI.AbilityAITools.ConsumerAlias);
        Assert.Equal(_site.Abilities.All.Count, tools.Count);
        Assert.All(tools, tool => Assert.Equal(
            tool.Descriptor.UsesChangeSet,
            tool.InputSchema.GetProperty("properties").TryGetProperty(AbilityTool.ChangeSetIdArgument, out _)));
    }

    [Fact]
    public void A_form_consumer_gets_whole_operations_and_no_building_blocks_change_set_or_explain_abilities()
    {
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);

        var explained = _site.Tools.Explain("workflows");
        var kept = explained.Where(t => !t.IsDropped).ToList();
        Assert.DoesNotContain(kept, t => t.Descriptor.Granular || t.Descriptor.ManagesChangeSets || t.Descriptor.Kind == AbilityKind.Explain);
        Assert.Contains(kept, t => t.Id == "email.send");
        Assert.Equal("hide-building-blocks", Tool("workflows", "entity.setValue", kept: false).Dropped!.Filter);
        Assert.Equal(("change-set-lifecycle", AbilityPolicyRegistry.Base), (Tool("workflows", "changeSet.apply", kept: false).Dropped!.Filter, Tool("workflows", "changeSet.apply", kept: false).Dropped!.Policy));
    }

    [Fact]
    public void A_form_consumer_gets_no_changeSetId_and_its_writes_are_each_a_change_set_of_their_own()
    {
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);

        var send = Tool("workflows", "email.send");
        Assert.Equal(ChangeSetMode.PerCall, send.ChangeSetMode);
        Assert.False(send.InputSchema.GetProperty("properties").TryGetProperty(AbilityTool.ChangeSetIdArgument, out _));
        Assert.All(_site.Tools.GetTools("workflows").Where(t => t.Descriptor.Kind != AbilityKind.Write), t => Assert.NotEqual(ChangeSetMode.Caller, t.ChangeSetMode));
    }

    [Fact]
    public async Task A_call_in_its_own_change_set_is_applied_at_once_and_one_that_fails_leaves_nothing_behind()
    {
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, "change-set-per-call");
        var create = Tool("workflows", "entity.create");
        var session = new AbilityToolSession(SampleAccess.Editor);

        var made = await _site.Runner.InvokeAsync(create, Json("""{ "entityType": "document", "variant": { "culture": "en-US" }, "fields": { "title": "Launch" } }"""), session, AbilityConsent.Given, changeSetName: "Step 1");
        Assert.True(made.Success, made.Message);
        Assert.Equal(("Step 1", false), (made.ChangeSet!.Name, made.ChangeSet.Open));
        var key = made.ToJson().GetProperty("result").GetProperty("key").GetGuid();
        Assert.NotNull(await _site.Pages.LoadAsync(key));

        var refused = await _site.Runner.InvokeAsync(create, Json("""{ "entityType": "document", "fields": { "nope": 1 } }"""), session, AbilityConsent.Given);
        Assert.False(refused.Success);
        Assert.Null(_site.ChangeSets.Find(refused.ChangeSet!.Id));
        Assert.Empty(session.OpenChangeSetIds);
    }

    [Fact]
    public void A_form_consumer_gets_a_Create_and_an_Update_per_entity_type_and_a_model_one_of_each()
    {
        AddConsumer("chat", AbilityAudiences.Model, Chat, FrameworkFilters.ForModels);
        AddConsumer("workflows", AbilityAudiences.Person, ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);

        var forms = _site.Tools.GetTools("workflows");
        var create = Assert.Single(forms, t => t.Id == "entity.create:document");
        Assert.Equal("Create Page", create.Title);
        Assert.Equal("document", create.FixedArguments["entityType"].GetString());
        Assert.False(create.InputSchema.GetProperty("properties").TryGetProperty("entityType", out _));
        Assert.Contains(forms, t => t.Id == "entity.update:umbraco-commerce-discount" && t.Title == "Update Discount");
        Assert.DoesNotContain(forms, t => t.Id is "entity.create" or "entity.update" or "entity.create:umbraco-commerce-collection");

        var chat = _site.Tools.GetTools("chat").Select(t => t.Id).ToList();
        Assert.Single(chat, id => id.StartsWith("entity.create", StringComparison.Ordinal));
        Assert.Single(chat, id => id.StartsWith("entity.update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_changes_only_the_values_given_in_one_change_set()
    {
        AddConsumer("workflows", AbilityAudiences.Person, ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);
        var session = new AbilityToolSession(SampleAccess.Editor);
        var made = await _site.Runner.InvokeAsync(
            Tool("workflows", "entity.create:document"),
            Json("""{ "variant": { "culture": "en-US" }, "fields": { "title": "Launch", "urlSegment": "launch" } }"""),
            session,
            AbilityConsent.Given);
        var key = made.ToJson().GetProperty("result").GetProperty("key").GetGuid();

        var updated = await _site.Runner.InvokeAsync(
            Tool("workflows", "entity.update:document"),
            Json($$"""{ "key": "{{key}}", "variant": { "culture": "en-US" }, "fields": { "title": "Launched" } }"""),
            session,
            AbilityConsent.Given,
            changeSetName: "Retitle");

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(("Retitle", false), (updated.ChangeSet!.Name, updated.ChangeSet.Open));
        var page = (await _site.Pages.LoadAsync(key))!.ToJson()["fields"]!;
        Assert.Equal("Launched", page["title"]!["en-US"]!.GetValue<string>());
        Assert.Equal("launch", page["urlSegment"]!["en-US"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_form_resolves_the_fields_input_to_the_entity_types_fields_with_person_hints()
    {
        AddConsumer("workflows", AbilityAudiences.Person, ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);

        var create = (await _site.Tools.ResolveInputSchemaAsync("workflows", "entity.create:umbraco-commerce-discount", []))!.Value;
        var update = (await _site.Tools.ResolveInputSchemaAsync("workflows", "entity.update:umbraco-commerce-discount", []))!.Value;

        var createFields = create.GetProperty("properties").GetProperty("fields");
        var updateFields = update.GetProperty("properties").GetProperty("fields");
        Assert.Contains("name", createFields.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Empty(updateFields.GetProperty("required").EnumerateArray());
        Assert.Equal(
            "Whether the discount is live. Leave it off to check the discount before customers get it. Leave it empty to keep it as it is.",
            updateFields.GetProperty("properties").GetProperty("active").GetProperty("description").GetString());
        Assert.False(create.GetProperty("properties").TryGetProperty("entityType", out _), "fixed by the split");
        Assert.False(create.GetProperty("properties").TryGetProperty("subtype", out _), "a discount has no subtypes");
        Assert.DoesNotContain(UiHints.HintsKeyword, create.GetRawText() + update.GetRawText());
    }

    [Fact]
    public void A_model_consumers_tools_have_no_keywords_meant_for_forms()
    {
        AddConsumer("chat", Chat, FrameworkFilters.ForModels);
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);

        Assert.Contains("x-editor", Tool("workflows", "email.send").InputSchema.GetRawText());
        Assert.All(_site.Tools.GetTools("chat"), t => Assert.DoesNotContain("\"x-editor\"", t.InputSchema.GetRawText()));
    }

    [Fact]
    public void An_admin_can_leave_an_ability_out_of_one_consumer_only()
    {
        AddConsumer("chat", Chat, FrameworkFilters.ForModels);
        AddConsumer("mcp", Chat, FrameworkFilters.ForModels);
        var tools = WithOverrides(new() { ["mcp"] = new ConsumerOverride { Exclude = ["commerce.*"] } });

        Assert.DoesNotContain(tools.GetTools("mcp"), t => t.Id.StartsWith("commerce.", StringComparison.Ordinal));
        Assert.Contains(tools.GetTools("chat"), t => t.Id.StartsWith("commerce.", StringComparison.Ordinal));
        Assert.Equal("override", tools.Explain("mcp").First(t => t.Id == "commerce.activateDiscount").Dropped!.Filter);
    }

    [Fact]
    public void An_admin_can_bring_back_what_a_filter_dropped_but_not_what_the_base_policy_dropped()
    {
        AddConsumer("workflows", ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);
        var tools = WithOverrides(new() { ["workflows"] = new ConsumerOverride { Include = ["entity.setValue", "changeSet.apply"] } });

        // Kept, and still shaped by the filters after the one that would have dropped it.
        var setValue = Assert.Single(tools.GetTools("workflows"), t => t.Id == "entity.setValue");
        Assert.Equal(ChangeSetMode.PerCall, setValue.ChangeSetMode);
        Assert.Contains(setValue.Notes, n => n.Filter == "override");

        Assert.DoesNotContain(tools.GetTools("workflows"), t => t.Id == "changeSet.apply");
    }

    [Fact]
    public void A_model_reads_the_description_with_model_hints_and_a_person_with_person_hints()
    {
        _site.Abilities.Add(new PublishPageAbility());
        AddConsumer("chat", AbilityAudiences.Model, Chat, FrameworkFilters.ForModels);
        AddConsumer("workflows", AbilityAudiences.Person, ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);

        var model = Tool("chat", "acme.publishPage");
        var person = Tool("workflows", "acme.publishPage");

        Assert.Equal("Puts a page live. Ask the person first.", model.Description);
        Assert.Equal("Puts a page live. Check the preview first.", person.Description);
        Assert.Equal("The page. Use entity.find to turn its name into a key.", PageDescription(model));
        Assert.Equal("The page. Pick it from the tree.", PageDescription(person));
        Assert.DoesNotContain(UiHints.HintsKeyword, model.InputSchema.GetRawText());
        Assert.DoesNotContain(UiHints.HintsKeyword, person.InputSchema.GetRawText());

        static string? PageDescription(AbilityTool tool) => tool.InputSchema.GetProperty("properties").GetProperty("page").GetProperty("description").GetString();
    }

    [Fact]
    public void Two_consumers_with_the_same_audience_get_the_same_hints()
    {
        _site.Abilities.Add(new PublishPageAbility());
        AddConsumer("chat", AbilityAudiences.Model, Chat, FrameworkFilters.ForModels);
        AddConsumer("model-forms", AbilityAudiences.Model, ConsumerCapabilities.StandingConsent, FrameworkFilters.ForForms);

        Assert.Equal(Tool("chat", "acme.publishPage").Description, Tool("model-forms", "acme.publishPage").Description);
        Assert.Equal(Tool("chat", "entity.getSchema").Description, Tool("model-forms", "entity.getSchema").Description);
    }

    [Fact]
    public async Task A_model_reading_a_schema_back_gets_model_hints_only()
    {
        AddConsumer("chat", AbilityAudiences.Model, Chat, FrameworkFilters.ForModels);
        var getSchema = Tool("chat", "entity.getSchema");

        var result = await _site.Runner.InvokeAsync(getSchema, Json("""{ "entityType": "umbraco-commerce-discount" }"""), new AbilityToolSession(SampleAccess.Editor));
        var shaped = _site.Tools.ShapeResult("chat", getSchema, result.ToJson());

        var active = shaped.GetProperty("result").GetProperty("fields").GetProperty("properties").GetProperty("active").GetProperty("description").GetString();
        Assert.Equal("Whether the discount is live. Use commerce.activateDiscount to switch it on.", active);
        Assert.DoesNotContain(UiHints.HintsKeyword, shaped.GetRawText());
    }

    private AbilityToolProvider WithOverrides(AbilityOverrides overrides)
        => new(_site.Abilities, _site.Filters, _site.Policies, _site.Consumers, overrides);

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private void AddConsumer(string alias, ConsumerCapabilities capabilities, params string[] filters)
        => AddConsumer(alias, AbilityAudiences.Model, capabilities, filters);

    private void AddConsumer(string alias, string audience, ConsumerCapabilities capabilities, params string[] filters)
        => _site.Consumers.Add(new AbilityConsumer(alias, alias, $"The {alias} consumer.", audience, capabilities, filters));

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

    public sealed record PublishPageInput(
        [property: Description("The page.")]
        [property: Hint(AbilityAudiences.Model, "Use entity.find to turn its name into a key.")]
        [property: Hint(AbilityAudiences.Person, "Pick it from the tree.")]
        Guid Page);

    // A package's ability with a hint for each reader, on itself and on its input.
    [Ability("acme.publishPage", "Publish page", Description = "Puts a page live.")]
    [Hint(AbilityAudiences.Model, "Ask the person first.")]
    [Hint(AbilityAudiences.Person, "Check the preview first.")]
    private sealed class PublishPageAbility : AbilityBase<PublishPageInput, object>
    {
        protected override Task<AbilityResult> ExecuteAsync(PublishPageInput input, AbilityContext context, CancellationToken cancellationToken)
            => Task.FromResult(AbilityResult.Ok(null));
    }
}
