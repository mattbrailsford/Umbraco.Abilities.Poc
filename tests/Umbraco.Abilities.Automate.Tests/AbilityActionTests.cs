using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

using NUnit.Framework;

using Umbraco.Abilities.Cms;
using Umbraco.Automate.Extensions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Automations;
using Umbraco.Automate.Core.Execution;
using Umbraco.Automate.Core.Runs;
using Umbraco.Automate.Core.Workspaces;
using Umbraco.Automate.Persistence;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Tests.Common.Builders;
using Umbraco.Cms.Tests.Common.Builders.Extensions;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;

namespace Umbraco.Abilities.Automate.Tests;

/// <summary>
/// The ability loader against a real Umbraco and real Umbraco Automate: every ability is an Automate action, with a
/// settings form and output schema from its JSON Schema, and an automation that uses them runs on Automate's own
/// workflow host, as the workspace's service account, and saves through the CMS adapter.
/// </summary>
[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, Boot = true)]
public sealed class AbilityActionTests : UmbracoIntegrationTest
{
    private ActionCollection Actions => GetRequiredService<ActionCollection>();

    private IContentService ContentService => GetRequiredService<IContentService>();

    // The test host doesn't run composers, so Automate is added as its composer would.
    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        builder.AddUmbracoAutomate();
        builder.AddAbilities();
        builder.Services.AddSingleton<StartedSignal>();
        builder.AddNotificationHandler<UmbracoApplicationStartedNotification, StartedSignal.Handler>();
    }

    private string? _automateDatabase;

    // Umbraco's test database is SQLite in memory, one connection's own. Automate makes and reads its tables through a
    // connection of its own, so it gets a database file for each test, as Commerce's tests give Commerce one.
    protected override void SetUpTestConfiguration(IConfigurationBuilder configBuilder)
    {
        base.SetUpTestConfiguration(configBuilder);
        _automateDatabase = Path.Combine(Path.GetTempPath(), $"abilities-automate-{Guid.NewGuid():N}.sqlite");
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoAutomateDbDSN"] = $"Data Source={_automateDatabase};Foreign Keys=True;Pooling=False",
            ["ConnectionStrings:umbracoAutomateDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",

            // Automate only runs steps on an elected server. A test host is never elected (it has no URL), so it's
            // the single server, as a one-server site can say.
            ["Umbraco:CMS:Global:DisableElectionForSingleServer"] = "true",
        });
    }

    [TearDown]
    public void DeleteAutomateDatabase()
    {
        if (_automateDatabase is not null && System.IO.File.Exists(_automateDatabase))
        {
            System.IO.File.Delete(_automateDatabase);
        }
    }

    // After Umbraco's builder has built, as a site's Program.cs would call it: only then is Automate's collection there.
    protected override void ConfigureTestServices(IServiceCollection services)
        => services.AddAbilityActions(options => options.FormatEditors["umbraco.cron"] = "Test.CronEditor");

    [SetUp]
    public async Task CreateArticleTypeAsync()
    {
        // Each test database needs an upgrade, which Umbraco runs in the background. Its starting handlers (the cache
        // rebuild among them) write to SQLite, and writing beside them deadlocks. "Started" comes after them all. Then
        // make sure Automate's tables are there: its own started handler races this set-up.
        await GetRequiredService<StartedSignal>().Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await GetRequiredService<IAutomateSchemaInitializer>().EnsureMigratedAsync();

        // An article: a mandatory title, and a writer picked from under an Authors page with Umbraco's tree picker.
        var writer = await CreateWriterPickerAsync();
        var article = new ContentTypeBuilder()
            .WithAlias("article").WithName("Article").WithContentVariation(ContentVariation.Nothing)
            .AddPropertyType()
                .WithAlias("title").WithName("Title")
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(ContentVariation.Nothing).WithMandatory(true)
                .Done()
            .AddPropertyType()
                .WithAlias("writer").WithName("Writer").WithDataTypeId(writer.Id)
                .WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.MultiNodeTreePicker).WithVariations(ContentVariation.Nothing)
                .Done()
            .Build();
        article.AllowedAsRoot = true;
        await GetRequiredService<IContentTypeService>().CreateAsync(article, Constants.Security.SuperUserKey);
    }

    private Guid _ann;

    // A tree picker that allows one page of type "author", from under an "Authors" page, with one author in it (_ann).
    private async Task<IDataType> CreateWriterPickerAsync()
    {
        var types = GetRequiredService<IContentTypeService>();
        var author = new ContentTypeBuilder().WithAlias("author").WithName("Author").WithContentVariation(ContentVariation.Nothing).Build();
        var authorList = new ContentTypeBuilder().WithAlias("authorList").WithName("Author list").WithContentVariation(ContentVariation.Nothing).Build();
        authorList.AllowedAsRoot = true;
        await types.CreateAsync(author, Constants.Security.SuperUserKey);
        authorList.AllowedContentTypes = [new ContentTypeSort(author.Key, 0, author.Alias)];
        await types.CreateAsync(authorList, Constants.Security.SuperUserKey);

        var authors = ContentService.Create("Authors", Constants.System.Root, authorList);
        ContentService.Save(authors);
        var ann = ContentService.Create("Ann Holm", authors.Id, author);
        ContentService.Save(ann);
        _ann = ann.Key;

        var dataType = new DataType(GetRequiredService<PropertyEditorCollection>()[Constants.PropertyEditors.Aliases.MultiNodeTreePicker], GetRequiredService<IConfigurationEditorJsonSerializer>())
        {
            ConfigurationData = new Dictionary<string, object>
            {
                { "startNode", new Dictionary<string, object> { { "type", "content" }, { "id", authors.Key.ToString() } } },
                { "filter", author.Key.ToString() },
                { "maxNumber", 1 },
            },
            Name = "Writer picker",

            // As the backoffice sets it when it makes a data type: the editor its UI shows.
            EditorUiAlias = "Umb.PropertyEditorUi.ContentPicker",
            DatabaseType = ValueStorageType.Ntext,
            ParentId = Constants.System.Root,
            CreateDate = DateTime.UtcNow,
        };
        await GetRequiredService<IDataTypeService>().CreateAsync(dataType, Constants.Security.SuperUserKey);
        return dataType;
    }

    [Test]
    public void Every_whole_ability_is_an_Automate_action_beside_Automates_own_and_building_blocks_are_not()
    {
        var aliases = Actions.Select(a => a.Alias).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(aliases, Has.Some.StartsWith("umbracoAutomate."), "Automate's own actions are still there");
            Assert.That(aliases, Does.Contain("abilities.entity.find").And.Contain("abilities.entity.delete"));

            // A step runs live, so the change set abilities aren't steps. Nor are the building blocks a model chains:
            // a person builds a step from a form, not from paths.
            Assert.That(aliases, Has.None.StartsWith("abilities.changeSet."));
            Assert.That(aliases, Has.None.EqualTo("abilities.entity.create").And.None.EqualTo("abilities.entity.setValue").And.None.EqualTo("abilities.entity.addItem"));
        });
    }

    [Test]
    public void Every_actions_output_schema_is_an_object_or_none_as_Automates_action_list_reads_it()
    {
        // Automate's editor lists actions with their output schemas, each read as an object. One that isn't breaks the
        // whole list. The entity edits return plain JSON, whose schema is "true".
        var notObjects = Actions
            .Where(a => a.GetOutputSchema() is { } schema && JsonSerializer.SerializeToElement(schema).ValueKind != JsonValueKind.Object)
            .Select(a => a.Alias);

        Assert.That(notObjects, Is.Empty);
    }

    [Test]
    public void Each_page_type_gets_a_Create_and_an_Update_action_grouped_under_its_entity_type()
    {
        var create = Actions.GetByAlias("abilities.document.create.article");
        var update = Actions.GetByAlias("abilities.document.update.article");

        Assert.That(create, Is.Not.Null, string.Join(", ", Actions.Select(a => a.Alias)));
        Assert.That(update, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(create!.Name, Is.EqualTo("Create Article"));
            Assert.That(update!.Name, Is.EqualTo("Update Article"));
            Assert.That(create.Group, Is.EqualTo(update.Group));
        });
    }

    [Test]
    public void A_Create_form_is_the_page_types_own_fields_with_Umbracos_editors_and_their_configuration()
    {
        var fields = Actions.GetByAlias("abilities.document.create.article")!.GetSettingsSchema()!.Fields.ToDictionary(f => f.Key);

        Assert.That(fields.Keys, Is.EquivalentTo(new[] { "name", "parent", "title", "writer" }));
        Assert.Multiple(() =>
        {
            Assert.That(fields["name"].IsRequired && fields["title"].IsRequired, Is.True);
            Assert.That(fields["writer"].IsRequired, Is.False);
            Assert.That(fields["title"].Label, Is.EqualTo("Title"));

            // Each property shows its data type's editor, configured as the data type is, as the backoffice does.
            Assert.That(fields["title"].EditorUiAlias, Is.EqualTo("Umb.PropertyEditorUi.TextBox"));
            Assert.That(fields["writer"].EditorUiAlias, Is.EqualTo("Umb.PropertyEditorUi.ContentPicker"));
            Assert.That(fields["writer"].EditorConfig, Does.Contain("startNode").And.Contain("maxNumber"));

            // The parent isn't a property, so it gets a picker from its reference.
            Assert.That(fields["parent"].EditorUiAlias, Is.EqualTo("Umb.PropertyEditorUi.DocumentPicker"));
        });
    }

    [Test]
    public void An_Update_form_picks_the_page_and_asks_for_no_value()
    {
        var fields = Actions.GetByAlias("abilities.document.update.article")!.GetSettingsSchema()!.Fields.ToList();

        Assert.That(fields[0].Key, Is.EqualTo("key"));
        Assert.That(fields[0].IsRequired, Is.True);
        Assert.That(fields.Skip(1).Where(f => f.IsRequired), Is.Empty, "a value left empty stays as it is");
    }

    [Test]
    public void A_Create_action_returns_a_typed_summary_a_later_step_can_bind_to()
    {
        var output = JsonSerializer.SerializeToElement(Actions.GetByAlias("abilities.document.create.article")!.GetOutputSchema());

        var properties = output.GetProperty("properties");
        Assert.That(properties.TryGetProperty("key", out _) && properties.TryGetProperty("name", out _), Is.True, output.ToString());
    }

    [Test]
    public async Task An_automation_creates_a_page_from_Umbracos_editor_values_then_updates_it_as_the_service_account()
    {
        // A workspace runs as its service account: an API user, here in the admin group.
        var users = GetRequiredService<IUserService>();
        var created = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
        {
            Email = "automate@example.com",
            UserName = "automate@example.com",
            Name = "Automate",
            Kind = UserKind.Api,
            UserGroupKeys = new HashSet<Guid> { Constants.Security.AdminGroupKey },
        }, approveUser: true);
        Assert.That(created.Success, Is.True, created.Status.ToString());
        var serviceAccount = created.Result.CreatedUser!;
        var workspace = await GetRequiredService<IWorkspaceService>().CreateWorkspaceAsync(new Workspace
        {
            Alias = "abilities",
            Name = "Abilities",
            ServiceAccountKey = serviceAccount.Key,
        });

        // Step one creates an article. Its writer is as Umbraco's tree picker gives it, so the adapter reads it. Step
        // two binds to the new page's key and sets only its title: the writer stays.
        var create = new StepConfiguration
        {
            Id = Guid.NewGuid(),
            Alias = "create",
            Name = "Create the article",
            ActionAlias = "abilities.document.create.article",
            Settings = Settings($$"""{ "name": "From Automate", "title": "First title", "writer": [{ "type": "document", "unique": "{{_ann}}" }] }"""),
            MaxRetries = 0,
        };
        var retitle = new StepConfiguration
        {
            Id = Guid.NewGuid(),
            Alias = "retitle",
            Name = "Retitle it",
            ActionAlias = "abilities.document.update.article",
            Settings = Settings("""{ "key": "${ steps.create.key }", "title": "Set by the second step", "writer": [] }"""),
            MaxRetries = 0,
        };

        var automations = GetRequiredService<IAutomationService>();
        var automation = await automations.CreateAutomationAsync(new Automation
        {
            Alias = "abilityPage",
            Name = "Ability page",
            WorkspaceId = workspace.Id,
            Trigger = new TriggerConfiguration { TriggerAlias = "umbracoAutomate.manual" },
            Steps = [create, retitle],
            Connections =
            [
                new StepConnection { SourceStepId = Guid.Empty, TargetStepId = create.Id },
                new StepConnection { SourceStepId = create.Id, TargetStepId = retitle.Id },
            ],
        });
        automation = await automations.PublishAutomationAsync(automation.Id);

        var runId = await GetRequiredService<IAutomationExecutor>().ExecuteAsync(automation, "User", null, null, CancellationToken.None);
        var run = await WaitForRunAsync(runId);

        Assert.That(run.Status, Is.EqualTo(AutomationRunStatus.Completed), Describe(run));
        var page = ContentService.GetRootContent().Single(c => c.Name == "From Automate");
        Assert.Multiple(() =>
        {
            Assert.That(page.GetValue<string>("title"), Is.EqualTo("Set by the second step"));
            Assert.That(page.GetValue<string>("writer"), Does.Contain(_ann.ToString("N")).Or.Contain(_ann.ToString()), "the picked writer, kept by the update");
            Assert.That(page.WriterId, Is.EqualTo(serviceAccount.Id), "saved as the workspace's service account");
        });
    }

    [Test]
    public async Task A_value_the_page_type_refuses_fails_the_step_with_the_fields_path_and_saves_nothing()
    {
        var users = GetRequiredService<IUserService>();
        var created = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
        {
            Email = "automate@example.com",
            UserName = "automate@example.com",
            Name = "Automate",
            Kind = UserKind.Api,
            UserGroupKeys = new HashSet<Guid> { Constants.Security.AdminGroupKey },
        }, approveUser: true);
        var workspace = await GetRequiredService<IWorkspaceService>().CreateWorkspaceAsync(new Workspace
        {
            Alias = "abilities",
            Name = "Abilities",
            ServiceAccountKey = created.Result.CreatedUser!.Key,
        });

        // No title, which is mandatory.
        var step = new StepConfiguration
        {
            Id = Guid.NewGuid(),
            Alias = "create",
            Name = "Create the article",
            ActionAlias = "abilities.document.create.article",
            Settings = Settings("""{ "name": "No title" }"""),
            MaxRetries = 0,
        };
        var automations = GetRequiredService<IAutomationService>();
        var automation = await automations.CreateAutomationAsync(new Automation
        {
            Alias = "noTitle",
            Name = "No title",
            WorkspaceId = workspace.Id,
            Trigger = new TriggerConfiguration { TriggerAlias = "umbracoAutomate.manual" },
            Steps = [step],
            Connections = [new StepConnection { SourceStepId = Guid.Empty, TargetStepId = step.Id }],
        });
        automation = await automations.PublishAutomationAsync(automation.Id);

        var run = await WaitForRunAsync(await GetRequiredService<IAutomationExecutor>().ExecuteAsync(automation, "User", null, null, CancellationToken.None));

        Assert.That(run.Status, Is.EqualTo(AutomationRunStatus.Failed), Describe(run));
        Assert.That(Describe(run), Does.Contain("title"));
        Assert.That(ContentService.GetRootContent().Where(c => c.Name == "No title"), Is.Empty);
    }

    // The run's error and each step's, for a failure message.
    private static string Describe(AutomationRun run)
        => $"{run.Status}: {run.Error} " + string.Join("; ", run.StepRuns.Select(s => $"{s.ActionAlias} {s.Status} {s.Error}"));

    private sealed class StartedSignal
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed class Handler(StartedSignal signal) : INotificationHandler<UmbracoApplicationStartedNotification>
        {
            public void Handle(UmbracoApplicationStartedNotification notification) => signal.Started.TrySetResult();
        }
    }

    private static Dictionary<string, object?> Settings(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(p => p.Key, p => (object?)p.Value);

    private async Task<AutomationRun> WaitForRunAsync(Guid runId)
    {
        var runs = GetRequiredService<IAutomationRunService>();
        var until = DateTime.UtcNow.AddSeconds(60);
        AutomationRun? run = null;
        while (DateTime.UtcNow < until)
        {
            run = await runs.GetRunAsync(runId);
            if (run is { Status: not (AutomationRunStatus.Pending or AutomationRunStatus.Running) })
            {
                return run;
            }

            await Task.Delay(250);
        }

        Assert.Fail("The run didn't finish within a minute. " + (run is null ? "No run." : Describe(run)));
        return null!;
    }
}
