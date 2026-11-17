using System.Text.Json;
using System.Text.Json.Nodes;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// A call that can't be undone needs the person's consent, on every surface. Without it the call does nothing and comes
/// back with a question and an approval id. The id approves that one call, once, and for a change set only the version
/// the person was shown.
/// </summary>
public class AbilityToolConsentTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly AbilityToolSession _session = new(SampleAccess.Editor);

    [Fact]
    public async Task A_call_that_cannot_be_undone_runs_only_with_the_approval_made_for_it()
    {
        var set = await StartAsync("Launch");
        await Tool("email.send").InvokeAsync(new JsonObject { ["to"] = "list@example.com", ["subject"] = "We launched" });

        var asked = await Tool("changeSet.apply").InvokeAsync([]);

        Assert.Equal(AbilityOperationStatus.ApprovalRequired, asked.Status);
        Assert.False(asked.Correctable);
        Assert.NotNull(_site.ChangeSets.Find(set));
        Assert.Empty(_site.Outbox.Sent);

        var applied = await Tool("changeSet.apply").InvokeAsync([], AbilityConsent.For(asked.Approval!.Id));

        Assert.True(applied.Success, applied.Message);
        Assert.Null(_site.ChangeSets.Find(set));
        Assert.Single(_site.Outbox.Sent);
    }

    [Fact]
    public async Task Asking_to_apply_a_set_shows_what_it_holds_and_names_what_could_not_be_undone_on_its_own()
    {
        await StartAsync("Summer");
        var discount = await CreateDiscountAsync("Summer sale");
        await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = discount.ToString() });
        await Tool("email.send").InvokeAsync(new JsonObject { ["to"] = "list@example.com", ["subject"] = "Summer sale is on" });

        var asked = await Tool("changeSet.apply").InvokeAsync([]);

        var question = asked.Approval!.Question;
        Assert.Contains("Discount \"Summer sale\" (created)", question);
        Assert.Contains("which can't be undone either", question);
        Assert.Contains("Activate discount: Summer sale", question);
        Assert.Contains("Summer sale is on", question);
        Assert.Equal(2, asked.Approval.Summary!.NeedsConsent!.Count);
    }

    [Fact]
    public async Task An_approval_for_a_change_set_is_refused_once_the_set_changed_and_the_person_is_asked_again()
    {
        var set = await StartAsync("Launch");
        await Tool("email.send").InvokeAsync(new JsonObject { ["to"] = "list@example.com", ["subject"] = "We launched" });
        var asked = await Tool("changeSet.apply").InvokeAsync([]);

        // Something is added after the person was asked, so they never saw it.
        await Tool("email.send").InvokeAsync(new JsonObject { ["to"] = "boss@example.com", ["subject"] = "Added later" });
        var stale = await Tool("changeSet.apply").InvokeAsync([], AbilityConsent.For(asked.Approval!.Id));

        Assert.Equal(AbilityOperationStatus.ApprovalRequired, stale.Status);
        Assert.Contains("changed since the person agreed", stale.Message);
        Assert.Contains("Added later", stale.Approval!.Question);
        Assert.NotNull(_site.ChangeSets.Find(set));
        Assert.Empty(_site.Outbox.Sent);
    }

    [Fact]
    public async Task An_approval_approves_only_its_own_call_and_only_once_so_several_can_wait_at_once()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var askedFirst = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = first });
        var askedSecond = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = second });

        // The first call's approval doesn't approve the second call.
        var swapped = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = second }, AbilityConsent.For(askedFirst.Approval!.Id));
        Assert.Equal(AbilityOperationStatus.ApprovalRequired, swapped.Status);
        Assert.Contains("different call", swapped.Message);

        // The second call's own approval does: the call runs (and fails, as the discount doesn't exist), once.
        var ran = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = second }, AbilityConsent.For(askedSecond.Approval!.Id));
        Assert.NotEqual(AbilityOperationStatus.ApprovalRequired, ran.Status);
        var again = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = second }, AbilityConsent.For(askedSecond.Approval.Id));
        Assert.Contains("already used", again.Message);
    }

    [Fact]
    public async Task An_approval_is_for_the_person_who_was_asked()
    {
        var discount = new JsonObject { ["discount"] = Guid.NewGuid().ToString() };
        var asked = await Tool("commerce.activateDiscount").InvokeAsync((JsonObject)discount.DeepClone());

        var someoneElse = new AbilityTool(_site.Abilities.Find("commerce.activateDiscount")!, _site.Invoker, new AbilityToolSession(SampleAccess.ContentEditor));
        var used = await someoneElse.InvokeAsync((JsonObject)discount.DeepClone(), AbilityConsent.For(asked.Approval!.Id));

        Assert.Equal(AbilityOperationStatus.ApprovalRequired, used.Status);
        Assert.Contains("someone else", used.Message);
    }

    [Fact]
    public async Task A_surface_that_asked_the_person_itself_needs_no_approval_id()
    {
        await StartAsync("Launch");

        var applied = await Tool("changeSet.apply").InvokeAsync([], AbilityConsent.Given);

        Assert.True(applied.Success, applied.Message);
    }

    [Fact]
    public async Task A_call_that_can_be_undone_needs_no_consent()
    {
        await StartAsync("Summer");

        var discarded = await Tool("changeSet.discard").InvokeAsync([]);

        Assert.True(discarded.Success, discarded.Message);
    }

    [Fact]
    public async Task A_call_that_needs_consent_live_needs_none_when_it_is_only_recorded_in_a_change_set()
    {
        var live = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = Guid.NewGuid().ToString() });
        Assert.Equal(AbilityOperationStatus.ApprovalRequired, live.Status);

        await StartAsync("Summer");
        var discount = await CreateDiscountAsync("Sale");

        var recorded = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = discount.ToString() });

        Assert.True(recorded.Success, recorded.Message);
    }

    private async Task<Guid> StartAsync(string name)
        => (await Tool("changeSet.create").InvokeAsync(new JsonObject { ["name"] = name })).ChangeSet!.Id;

    private async Task<Guid> CreateDiscountAsync(string name)
    {
        var created = await Tool("entity.create").InvokeAsync(new JsonObject
        {
            ["entityType"] = "umbraco-commerce-discount",
            ["fields"] = new JsonObject { ["name"] = name },
        });
        Assert.True(created.Success, created.Message);
        return JsonSerializer.SerializeToElement(created.Result, AbilityJson.Options).GetProperty("key").GetGuid();
    }

    private AbilityTool Tool(string id) => new(_site.Abilities.Find(id)!, _site.Invoker, _session);
}
