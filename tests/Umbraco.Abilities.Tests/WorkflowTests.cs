using Umbraco.Abilities.Entities;
using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// An Automate workflow is an entity whose steps are abilities and Automate's own steps, linked as a graph by item
/// references: <c>next</c> for a step with one exit, or an <c>outcomes</c> object for a step that names outcomes.
/// </summary>
public class WorkflowTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly ScriptedAgent _agent;

    public WorkflowTests() => _agent = new ScriptedAgent(_site);

    [Fact]
    public async Task Step_types_are_derived_from_abilities()
    {
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "umbraco-automate-workflow", "itemTypes": ["email.send"] }""");
        var stepTypes = schema.GetProperty("fields").GetProperty("properties").GetProperty("steps").GetProperty("x-item-types").EnumerateArray().Select(t => t.GetProperty("alias").GetString()).ToList();
        // Abilities, then Automate's actions, then its control flows.
        Assert.Equal(["email.send", "commerce.activateDiscount", "umbracoAutomate.getContent", "umbracoCommerce.changeOrderStatus", "umbracoAutomate.if"], stepTypes);

        var email = schema.GetProperty("itemTypes").GetProperty("email.send").GetProperty("fields");
        Assert.Equal(["to", "subject", "body", "next"], email.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["to", "subject"], email.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_workflow_from_text_links_its_steps_and_can_refer_to_a_discount_in_the_same_set()
    {
        // "When someone signs up for the newsletter, email them, then switch on the welcome discount."
        await _agent.StartChangeSetAsync("Welcome flow");

        var discount = (await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-commerce-discount", "fields": { "name": "Welcome 10%" } }""")).GetProperty("key").GetGuid();
        await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "umbraco-commerce-discount", "key": "{{discount}}", "fieldPath": "rules", "itemType": "orderTotal", "fields": { "minTotal": 0 } }""");
        await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "umbraco-commerce-discount", "key": "{{discount}}", "fieldPath": "rewards", "itemType": "percentageOff", "fields": { "percent": 10 } }""");

        var workflow = (await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-automate-workflow", "fields": { "name": "Welcome flow", "enabled": true } }""")).GetProperty("key").GetGuid();
        await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "trigger", "itemType": "formSubmitted", "fields": { "form": "Newsletter" } }
            """);

        var emailStep = Guid.NewGuid();
        var activateStep = Guid.NewGuid();
        await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "email.send", "itemKey": "{{emailStep}}",
              "fields": { "to": "${email}", "subject": "Welcome", "next": "{{activateStep}}" } }
            """);

        // The edge points at a step that does not exist yet.
        var validation = await _agent.OkAsync("changeSet_validate", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.Equal([$"steps/{emailStep}/next"], JsonAssert.ProblemPaths(validation));

        await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "commerce.activateDiscount", "itemKey": "{{activateStep}}",
              "fields": { "discount": "{{discount}}" } }
            """);

        validation = await _agent.OkAsync("changeSet_validate", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.True(validation.GetProperty("valid").GetBoolean(), validation.ToString());

        await _agent.OkAsync("changeSet_apply", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        var live = (await _site.Workflows.LoadAsync(workflow))!;
        Assert.Equal(["email.send", "commerce.activateDiscount"], live.Fields["steps"]!.AsArray().Select(s => s!["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_step_referring_to_a_missing_discount_is_a_problem()
    {
        await _agent.StartChangeSetAsync("Broken flow");
        var workflow = (await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-automate-workflow", "fields": { "name": "Broken" } }""")).GetProperty("key").GetGuid();
        await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "trigger", "itemType": "schedule", "fields": { "cron": "0 9 * * 1" } }""");
        var step = await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "commerce.activateDiscount", "fields": { "discount": "{{Guid.NewGuid()}}" } }
            """);

        var validation = await _agent.OkAsync("changeSet_validate", $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""");
        Assert.Equal([$"{step.GetProperty("path").GetString()}/discount"], JsonAssert.ProblemPaths(validation));
    }

    [Fact]
    public async Task A_step_that_names_outcomes_has_one_exit_per_outcome_and_no_next()
    {
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "umbraco-automate-workflow", "itemTypes": ["umbracoAutomate.if", "umbracoAutomate.getContent", "email.send"] }""");
        var itemTypes = schema.GetProperty("itemTypes");
        string[] Fields(string alias) => itemTypes.GetProperty(alias).GetProperty("fields").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["conditions", "outcomes"], Fields("umbracoAutomate.if"));
        Assert.Equal(["contentKey", "outcomes"], Fields("umbracoAutomate.getContent"));
        Assert.Equal(["to", "subject", "body", "next"], Fields("email.send"));

        var outcomes = itemTypes.GetProperty("umbracoAutomate.if").GetProperty("fields").GetProperty("properties").GetProperty("outcomes").GetProperty("properties");
        Assert.Equal(["true", "false"], outcomes.EnumerateObject().Select(p => p.Name));
        var whenFalse = outcomes.GetProperty("false");
        Assert.Equal("steps", whenFalse.GetProperty("x-references-item-in").GetString());
        Assert.Contains("'false'", whenFalse.GetProperty("description").GetString());

        // An outcome the step doesn't have is refused, and the message lists the ones it does.
        await _agent.StartChangeSetAsync("Wrong exit");
        var workflow = (await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-automate-workflow", "fields": { "name": "Wrong exit" } }""")).GetProperty("key").GetGuid();
        var refused = await _agent.FailAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "umbracoAutomate.if",
              "fields": { "conditions": { "groups": [] }, "outcomes": { "yes": "{{Guid.NewGuid()}}" } } }
            """);
        Assert.Contains("Fields: true, false.", refused.GetProperty("problems")[0].GetProperty("errorMessages")[0].GetString());

        // So is next: a step that names outcomes routes only by them.
        refused = await _agent.FailAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "umbracoAutomate.getContent",
              "fields": { "contentKey": "${trigger.contentKey}", "next": "{{Guid.NewGuid()}}" } }
            """);
        Assert.Contains("Fields: contentKey, outcomes.", refused.GetProperty("problems")[0].GetProperty("errorMessages")[0].GetString());
    }

    [Fact]
    public async Task A_branching_workflow_is_saved_as_Automates_connections_with_outcomes_and_loaded_back_as_exits()
    {
        // "When an order is created, if its total is over 1,000, email sales. Otherwise, set its status to Processing."
        await _agent.StartChangeSetAsync("Big orders");
        var workflow = (await _agent.OkAsync("entity_create", """{ "entityType": "umbraco-automate-workflow", "fields": { "name": "Big orders", "enabled": true } }""")).GetProperty("key").GetGuid();
        await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "trigger", "itemType": "umbracoCommerce.orderCreated" }""");

        var check = Guid.NewGuid();
        var email = Guid.NewGuid();
        var status = Guid.NewGuid();
        await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "umbracoAutomate.if", "itemKey": "{{check}}",
              "fields": {
                "conditions": { "groups": [ { "conditions": [ { "leftOperand": "${trigger.orderTotal}", "operator": "greaterThan", "rightOperand": "1000" } ] } ] },
                "outcomes": { "true": "{{email}}", "false": "{{status}}" } } }
            """);
        await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "email.send", "itemKey": "{{email}}",
              "fields": { "to": "sales@example.com", "subject": "Big order ${trigger.orderId}" } }
            """);

        // The false exit points at a step not added yet.
        var validation = await _agent.OkAsync("changeSet_validate", "{}");
        Assert.Equal([$"steps/{check}/outcomes/false"], JsonAssert.ProblemPaths(validation));

        await _agent.OkAsync("entity_addItem", $$"""
            { "entityType": "umbraco-automate-workflow", "key": "{{workflow}}", "fieldPath": "steps", "itemType": "umbracoCommerce.changeOrderStatus", "itemKey": "{{status}}",
              "fields": { "orderId": "${trigger.orderId}", "orderStatusId": "{{Guid.NewGuid()}}" } }
            """);
        validation = await _agent.OkAsync("changeSet_validate", "{}");
        Assert.True(validation.GetProperty("valid").GetBoolean(), validation.ToString());
        await _agent.OkAsync("changeSet_apply", "{}");

        // Stored as Automate stores it: steps without exits, and connections that name the outcome.
        var stored = _site.AutomateDatabase.Get("umbraco-automate-workflow", workflow)!.Root;
        Assert.All(stored["steps"]!.AsArray(), step => Assert.False(step!.AsObject().ContainsKey("outcomes") || step.AsObject().ContainsKey("next")));
        Assert.Equal(
            [$"{check} → {email} (true)", $"{check} → {status} (false)"],
            stored["connections"]!.AsArray().Select(c => $"{c!["sourceStepId"]} → {c["targetStepId"]} ({c["outcome"]})"));

        // Loaded back, each connection is the exit its outcome names.
        var live = (await _site.Workflows.LoadAsync(workflow))!;
        var ifStep = live.Fields["steps"]!.AsArray().Single(s => s!["key"]!.GetValue<string>() == check.ToString())!;
        Assert.Equal(email.ToString(), ifStep["outcomes"]!["true"]!.GetValue<string>());
        Assert.Equal(status.ToString(), ifStep["outcomes"]!["false"]!.GetValue<string>());
    }
}
