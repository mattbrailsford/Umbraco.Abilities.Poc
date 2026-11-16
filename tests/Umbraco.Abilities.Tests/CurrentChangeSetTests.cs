using System.Text.Json;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// Which change set a call works in. A call names one with the generic <c>changeSetId</c> argument, or works in the
/// conversation's current set: the one last created or named. Within the call it is ambient, through the change set
/// accessor, so no ability takes the id as input. A real model found the gap this closes: it was never told the id of
/// the set it worked in, and a set it created itself could not be used.
/// </summary>
public class CurrentChangeSetTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly ScriptedAgent _agent;

    public CurrentChangeSetTests() => _agent = new ScriptedAgent(_site);

    [Fact]
    public async Task A_change_set_the_model_creates_is_used_by_later_calls_without_its_id()
    {
        var created = await _agent.CallAsync("changeSet_create", """{ "name": "VIP sale" }""");
        var setId = created.GetProperty("result").GetProperty("changeSetId").GetGuid();
        Assert.Equal(setId, created.GetProperty("changeSet").GetProperty("id").GetGuid());

        var edit = await _agent.CallAsync("entity_create", """{ "entityType": "umbraco-commerce-discount", "fields": { "name": "VIP sale" } }""");

        Assert.True(edit.GetProperty("success").GetBoolean(), edit.ToString());
        Assert.Equal(setId, edit.GetProperty("changeSet").GetProperty("id").GetGuid());
        Assert.Equal("VIP sale", edit.GetProperty("changeSet").GetProperty("name").GetString());
        Assert.Single(_site.ChangeSets.Get(setId).Changes);
        Assert.Empty(_site.DiscountService.GetDiscounts());

        // Lifecycle tools need no id either.
        var validation = await _agent.OkAsync("changeSet_validate", "{}");
        Assert.Contains("rules", JsonAssert.ProblemPaths(validation));
    }

    [Fact]
    public async Task Two_change_sets_in_one_conversation_are_kept_apart_by_naming_one()
    {
        var christmas = await _agent.StartChangeSetAsync("Christmas");
        var blackFriday = await _agent.StartChangeSetAsync("Black Friday");

        // The set created last is current.
        await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-commerce-discount", "fields": { "name": "Black Friday" } }""");

        // Naming a set works in it, and makes it current for the calls that follow.
        var named = await _agent.CallAsync("entity_create", $$"""{ "changeSetId": "{{christmas}}", "entityType": "umbraco-commerce-discount", "fields": { "name": "Christmas" } }""");
        Assert.Equal(christmas, named.GetProperty("changeSet").GetProperty("id").GetGuid());
        await _agent.OkAsync("entity_create", """{ "entityType": "document", "variant": { "culture": "en-US" }, "fields": { "title": "Christmas gifts" } }""");

        Assert.Single(_site.ChangeSets.Get(blackFriday).Changes);
        Assert.Equal(2, _site.ChangeSets.Get(christmas).Changes.Count);
    }

    [Fact]
    public async Task An_unknown_change_set_id_is_refused_and_nothing_is_written_live()
    {
        var current = await _agent.StartChangeSetAsync("VIP sale");

        var refused = await _agent.FailAsync("entity_create", $$"""{ "changeSetId": "{{Guid.NewGuid()}}", "entityType": "umbraco-commerce-discount", "fields": { "name": "Typo" } }""");

        Assert.True(refused.GetProperty("correctable").GetBoolean());
        Assert.Equal("changeSetId", refused.GetProperty("problems")[0].GetProperty("path").GetString());
        Assert.Empty(_site.DiscountService.GetDiscounts());
        Assert.Empty(_site.ChangeSets.Get(current).Changes);
        Assert.Equal(current, _agent.ChangeSetId);
    }

    [Fact]
    public async Task Once_the_current_set_is_applied_the_conversation_has_none_until_another_is_created()
    {
        await _agent.StartChangeSetAsync("Collections");
        await _agent.OkAsync("entity_create", """{ "entityType": "document", "variant": { "culture": "en-US" }, "fields": { "title": "Spring" } }""");

        var applied = await _agent.CallAsync("changeSet_apply", "{}");

        Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());
        Assert.False(applied.GetProperty("changeSet").GetProperty("open").GetBoolean());
        Assert.Null(_agent.ChangeSetId);

        // An edit now has nowhere to go, and says how to fix that, rather than writing live.
        var edit = await _agent.FailAsync("entity_create", """{ "entityType": "document", "variant": { "culture": "en-US" }, "fields": { "title": "Summer" } }""");
        Assert.Contains("changeSet_create", edit.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, edit.GetProperty("changeSet").ValueKind);
    }

    [Fact]
    public async Task The_current_set_is_ambient_for_one_call_only_and_each_conversation_has_its_own()
    {
        var other = new ScriptedAgent(_site);
        var mine = await _agent.StartChangeSetAsync("Mine");
        var theirs = await other.StartChangeSetAsync("Theirs");

        await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-commerce-discount", "fields": { "name": "Mine" } }""");
        await other.OkAsync("entity_create", """{ "entityType": "umbraco-commerce-discount", "fields": { "name": "Theirs" } }""");

        Assert.Single(_site.ChangeSets.Get(mine).Changes);
        Assert.Single(_site.ChangeSets.Get(theirs).Changes);
        Assert.Null(_site.ChangeSetAccessor.CurrentChangeSetId);
    }

    [Fact]
    public async Task Change_set_tools_ask_for_a_set_when_none_is_open()
    {
        var failure = await _agent.FailAsync("changeSet_validate", "{}");

        Assert.True(failure.GetProperty("correctable").GetBoolean());
        Assert.Equal("changeSetId", failure.GetProperty("problems")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Every_tool_but_create_takes_an_optional_change_set_id_that_no_ability_declares()
    {
        foreach (var (name, tool) in _agent.Tools)
        {
            var properties = tool.JsonSchema.GetProperty("properties");
            var required = tool.JsonSchema.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(r => r.GetString()).ToList() : [];

            Assert.Equal(name != "changeSet_create", properties.TryGetProperty("changeSetId", out _));
            Assert.DoesNotContain("changeSetId", required);
        }
    }
}
