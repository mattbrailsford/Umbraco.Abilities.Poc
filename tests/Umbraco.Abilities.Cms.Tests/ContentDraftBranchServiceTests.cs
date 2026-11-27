using NUnit.Framework;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.DraftSets;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;

namespace Umbraco.Abilities.Cms.Tests;

/// <summary>
/// The branch stand-in behaves as the requirement tests of CMS PR #24806 ask, so <see cref="CmsDocumentAdapterTests"/>
/// passing on it shows change sets work on branches that meet them.
/// </summary>
[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, WithApplication = true)]
public sealed class ContentDraftBranchServiceTests : UmbracoIntegrationTestWithContent
{
    private ContentDraftBranchService _branches = null!;

    private IEntityService EntityService => GetRequiredService<IEntityService>();

    [SetUp]
    public void SetUpBranches() => _branches = new ContentDraftBranchService(
        GetRequiredService<IContentEditingService>(),
        GetRequiredService<PropertyEditorCollection>(),
        GetRequiredService<IJsonSerializer>(),
        GetRequiredService<ICoreScopeProvider>(),
        GetRequiredService<IEventAggregator>(),
        GetRequiredService<IIdKeyMap>());

    [Test]
    public async Task Saving_a_branch_changes_nothing_on_the_node_and_keeps_its_base()
    {
        var branch = (await _branches.CreateAsync(Textpage.Key, "Black Friday"))!;
        var content = _branches.GetContent(Textpage.Key, branch.Key)!;
        content.Names[string.Empty] = "Black Friday Edit";
        content.ParentKey = Subpage.Key;
        _branches.Save(Textpage.Key, branch.Key, content);

        var page = ContentService.GetById(Textpage.Key)!;
        page.Name = "Changed after fork";
        ContentService.Save(page);

        Assert.Multiple(() =>
        {
            Assert.That(EntityService.Get(Subpage2.Key)!.ParentId, Is.EqualTo(Textpage.Id), "not moved");
            Assert.That(EntityService.Get(Textpage.Key)!.Name, Is.EqualTo("Changed after fork"), "tree name");
            Assert.That(ContentService.GetVersions(Textpage.Id).Select(v => v.Name), Does.Not.Contain("Black Friday Edit"), "history");
            Assert.That(_branches.GetBase(Textpage.Key, branch.Key)!.Names[string.Empty], Is.EqualTo("Textpage"), "base");
        });
    }

    [Test]
    public async Task Promoting_keeps_changes_made_to_other_values_on_the_default_draft()
    {
        var branch = (await _branches.CreateAsync(Textpage.Key, "Black Friday"))!;
        var content = _branches.GetContent(Textpage.Key, branch.Key)!;
        content.Names[string.Empty] = "Changed in the branch";
        _branches.Save(Textpage.Key, branch.Key, content);

        var page = ContentService.GetById(Textpage.Key)!;
        page.SetValue("author", "Changed on the default draft");
        ContentService.Save(page);

        Assert.That(await _branches.PromoteAsync(Textpage.Key, branch.Key, Constants.Security.SuperUserKey), Is.True);
        page = ContentService.GetById(Textpage.Key)!;
        Assert.Multiple(() =>
        {
            Assert.That(page.Name, Is.EqualTo("Changed in the branch"));
            Assert.That(page.GetValue<string>("author"), Is.EqualTo("Changed on the default draft"));
            Assert.That(_branches.GetByKey(Textpage.Key, branch.Key), Is.Null, "promoted branches go");
        });
    }

    [Test]
    public async Task A_promote_in_an_outer_scope_that_rolls_back_saves_nothing_and_keeps_the_branch()
    {
        var branch = (await _branches.CreateAsync(Subpage.Key, "Black Friday"))!;
        var content = _branches.GetContent(Subpage.Key, branch.Key)!;
        content.Names[string.Empty] = "Promoted then rolled back";
        _branches.Save(Subpage.Key, branch.Key, content);

        using (GetRequiredService<ICoreScopeProvider>().CreateCoreScope())
        {
            await _branches.PromoteAsync(Subpage.Key, branch.Key, Constants.Security.SuperUserKey);
        }

        Assert.Multiple(() =>
        {
            Assert.That(ContentService.GetById(Subpage.Key)!.Name, Is.EqualTo("Text Page 1"));
            Assert.That(_branches.GetByKey(Subpage.Key, branch.Key), Is.Not.Null);
        });
    }
}
