using System.Text.Json.Nodes;

using Umbraco.Abilities.Samples;

namespace Umbraco.Abilities.Tests;

/// <summary>
/// A call that can't be undone needs the person's consent, on every surface. The surface asks; the framework refuses
/// a call that comes without it, so a surface that forgets to ask can't post by accident.
/// </summary>
public class AbilityToolConsentTests
{
    private readonly SampleSite _site = SampleSite.Create();
    private readonly AbilityToolSession _session = new(SampleAccess.Editor);

    [Fact]
    public async Task A_call_that_cannot_be_undone_runs_only_when_the_surface_says_the_person_agreed()
    {
        var created = await Tool("changeSet.create").InvokeAsync(new JsonObject { ["name"] = "Summer" });
        var set = created.ChangeSet!.Id;

        var refused = await Tool("changeSet.apply").InvokeAsync([]);

        Assert.False(refused.Success);
        Assert.Equal(AbilityOperationStatus.ApprovalRequired, refused.Status);
        Assert.False(refused.Correctable);
        Assert.Equal(set, refused.ChangeSet!.Id);
        Assert.NotNull(_site.ChangeSets.Find(set));

        var applied = await Tool("changeSet.apply").InvokeAsync([], approved: true);

        Assert.True(applied.Success, applied.Message);
        Assert.Null(_site.ChangeSets.Find(set));
    }

    [Fact]
    public async Task A_call_that_can_be_undone_needs_no_consent()
    {
        await Tool("changeSet.create").InvokeAsync(new JsonObject { ["name"] = "Summer" });

        var discarded = await Tool("changeSet.discard").InvokeAsync([]);

        Assert.True(discarded.Success, discarded.Message);
    }

    [Fact]
    public async Task A_call_that_needs_consent_live_needs_none_when_it_is_only_recorded_in_a_change_set()
    {
        var discount = new JsonObject { ["discount"] = Guid.NewGuid().ToString() };

        var live = await Tool("commerce.activateDiscount").InvokeAsync(discount.DeepClone().AsObject());
        Assert.Equal(AbilityOperationStatus.ApprovalRequired, live.Status);

        await Tool("changeSet.create").InvokeAsync(new JsonObject { ["name"] = "Summer" });
        var created = await Tool("entity.create").InvokeAsync(new JsonObject
        {
            ["entityType"] = "umbraco-commerce-discount",
            ["fields"] = new JsonObject { ["name"] = "Sale" },
        });
        Assert.True(created.Success, created.Message);
        var key = System.Text.Json.JsonSerializer.SerializeToElement(created.Result, AbilityJson.Options).GetProperty("key").GetGuid();

        var recorded = await Tool("commerce.activateDiscount").InvokeAsync(new JsonObject { ["discount"] = key.ToString() });

        Assert.True(recorded.Success, recorded.Message);
    }

    private AbilityTool Tool(string id) => new(_site.Abilities.Find(id)!, _site.Invoker, _session);
}
