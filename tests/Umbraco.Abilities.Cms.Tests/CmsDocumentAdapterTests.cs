using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

using NUnit.Framework;

using Umbraco.Abilities.AI;
using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.DynamicRoot;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.HostedServices;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.DraftSets;
using Umbraco.Cms.Infrastructure.HostedServices;
using Umbraco.Cms.Tests.Common.Builders;
using Umbraco.Cms.Tests.Common.Builders.Extensions;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;
using Umbraco.Extensions;
namespace Umbraco.Abilities.Cms.Tests;

/// <summary>
/// The CMS document adapter against a real Umbraco (SQLite): culture-variant documents with a Block List
/// (blocks with culture-variant values and a settings element) and a Block Grid (placement and areas), built
/// through the AI tools in a change set, validated by Umbraco, and saved through IContentEditingService.
/// </summary>
/// <remarks>
/// Runs twice: on the draft set stand-in that records changed values itself, and on the one built on named draft
/// branches (a stand-in for CMS PR #24784, as if the requirement tests of PR #24806 pass). Both must pass every test.
/// </remarks>
[TestFixture(DraftSetKind.Values)]
[TestFixture(DraftSetKind.Branches)]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, WithApplication = true)]
public sealed class CmsDocumentAdapterTests(CmsDocumentAdapterTests.DraftSetKind draftSetKind) : UmbracoIntegrationTest
{
    public enum DraftSetKind
    {
        Values,
        Branches,
    }

    private IContentType _page = null!;
    private Agent _agent = null!;
    private CmsDocumentAdapter _documents = null!;
    private CmsMediaAdapter _media = null!;
    private ChangeSetService _changeSets = null!;
    private ChangeSetAccessor _accessor = null!;
    private IDraftSetService _draftSets = null!;
    private IDataType _pageBlocks = null!;
    private Guid _ann;

    private IContentTypeService ContentTypeService => GetRequiredService<IContentTypeService>();

    private IContentService ContentService => GetRequiredService<IContentService>();

    private IContentEditingService ContentEditingService => GetRequiredService<IContentEditingService>();

    // A third-party handler that vetoes saving a page called "Refused", to make a save fail after validation.
    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        builder.AddNotificationHandler<ContentSavingNotification, RefuseSavingHandler>();

        // A package's property editor, which Umbraco knows only as a JSON value.
        builder.DataEditors().Add<StarRatingPropertyEditor>();

        // Property editor handlers are found on their own, the package's star rating handler included. A site runs
        // this composer; the test host doesn't run package composers.
        new PropertyEditorEntityHandlersComposer().Compose(builder);

        // A package's handlers: veto sets with certain names, and record what was applied.
        builder.AddNotificationHandler<ChangeSetApplyingNotification, VetoHandler>();
        builder.AddNotificationHandler<DraftSetApplyingNotification, VetoHandler>();
        builder.AddNotificationHandler<ChangeSetAppliedNotification, VetoHandler>();
        builder.AddNotificationHandler<DraftSetAppliedNotification, VetoHandler>();
        builder.AddNotificationHandler<ChangeSetSideEffectCompletedNotification, VetoHandler>();
    }

    protected override void SetUpTestConfiguration(IConfigurationBuilder configBuilder)
    {
        base.SetUpTestConfiguration(configBuilder);
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tests:Database:DatabaseType"] = "Sqlite",
            ["Tests:Database:PrepareThreadCount"] = "4",
            ["Tests:Database:SchemaDatabaseCount"] = "4",
            ["Tests:Database:EmptyDatabasesCount"] = "2",
        });
    }

    [SetUp]
    public async Task SetUpSiteAsync()
    {
        await GetRequiredService<ILanguageService>().CreateAsync(new Language("da-DK", "Danish"), Constants.Security.SuperUserKey);

        // Blocks: a hero whose heading varies by culture and that has a settings element; a text block;
        // and a two-column grid row with areas.
        var hero = await CreateElementTypeAsync("hero", ContentVariation.Culture, ("heading", ContentVariation.Culture, true));
        var heroSettings = await CreateElementTypeAsync("heroSettings", ContentVariation.Nothing, ("theme", ContentVariation.Nothing, false));
        var text = await CreateElementTypeAsync("textBlock", ContentVariation.Culture, ("text", ContentVariation.Culture, false));
        var row = await CreateElementTypeAsync("twoColumns", ContentVariation.Nothing, ("label", ContentVariation.Nothing, false));

        // A block with a block property of its own: a group of cards, each with a mandatory title.
        var card = await CreateElementTypeAsync("card", ContentVariation.Nothing, ("title", ContentVariation.Nothing, true));
        var cards = await CreateDataTypeAsync(Constants.PropertyEditors.Aliases.BlockList, "Cards", new BlockListConfiguration.BlockConfiguration[] { new() { ContentElementTypeKey = card.Key } });
        var cardGroup = new ContentTypeBuilder().WithAlias("cardGroup").WithName("cardGroup").WithIsElement(true).WithContentVariation(ContentVariation.Nothing)
            .AddPropertyType().WithAlias("cards").WithName("cards").WithDataTypeId(cards.Id).WithVariations(ContentVariation.Nothing).Done()
            .Build();
        await ContentTypeService.CreateAsync(cardGroup, Constants.Security.SuperUserKey);

        var blockList = _pageBlocks = await CreateDataTypeAsync(Constants.PropertyEditors.Aliases.BlockList, "Page blocks", new BlockListConfiguration.BlockConfiguration[]
        {
            new() { ContentElementTypeKey = hero.Key, SettingsElementTypeKey = heroSettings.Key },
            new() { ContentElementTypeKey = text.Key },
            new() { ContentElementTypeKey = cardGroup.Key },
        });
        var blockGrid = await CreateDataTypeAsync(Constants.PropertyEditors.Aliases.BlockGrid, "Page grid", new BlockGridConfiguration.BlockGridBlockConfiguration[]
        {
            new()
            {
                ContentElementTypeKey = row.Key,
                AllowAtRoot = true,
                AreaGridColumns = 12,
                Areas =
                [
                    new() { Key = Guid.NewGuid(), Alias = "left", ColumnSpan = 6, RowSpan = 1, MaxAllowed = 2 },
                    new() { Key = Guid.NewGuid(), Alias = "right", ColumnSpan = 6, RowSpan = 1 },
                ],
            },
            new() { ContentElementTypeKey = text.Key, AllowAtRoot = true, AllowInAreas = true },
        });

        // Editors the mapper has no rule for. Umbraco describes each one's value with a schema.
        var accent = await CreateEditorDataTypeAsync(Constants.PropertyEditors.Aliases.ColorPickerEyeDropper, "Accent colour");
        var rating = await CreateEditorDataTypeAsync(StarRatingPropertyEditor.EditorAlias, "Star rating");

        // A tree picker of one author, from under the Authors page, as Clean's article has.
        var writer = await CreateTreePickerAsync("Writer");

        _page = new ContentTypeBuilder()
            .WithAlias("landingPage")
            .WithName("Landing page")
            .WithContentVariation(ContentVariation.Culture)
            .AddPropertyType()
                .WithAlias("subtitle").WithName("Subtitle")
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(ContentVariation.Culture).WithMandatory(true)
                .Done()
            .AddPropertyType()
                .WithAlias("blocks").WithName("Blocks").WithDataTypeId(blockList.Id).WithVariations(ContentVariation.Nothing)
                .Done()
            .AddPropertyType()
                .WithAlias("grid").WithName("Grid").WithDataTypeId(blockGrid.Id).WithVariations(ContentVariation.Nothing)
                .Done()
            .AddPropertyType()
                .WithAlias("accent").WithName("Accent").WithDataTypeId(accent.Id)
                .WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.ColorPickerEyeDropper).WithVariations(ContentVariation.Nothing)
                .Done()
            .AddPropertyType()
                .WithAlias("launchDate").WithName("Launch date").WithDataTypeId(Constants.DataTypes.DateTime)
                .WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.DateTime).WithVariations(ContentVariation.Nothing)
                .Done()
            .AddPropertyType()
                .WithAlias("size").WithName("Size").WithDataTypeId(Constants.DataTypes.DropDownSingle)
                .WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.DropDownListFlexible).WithVariations(ContentVariation.Nothing)
                .Done()
            .AddPropertyType()
                .WithAlias("rating").WithName("Rating").WithDataTypeId(rating.Id)
                .WithPropertyEditorAlias(StarRatingPropertyEditor.EditorAlias).WithVariations(ContentVariation.Nothing)
                .Done()
            .AddPropertyType()
                .WithAlias("writer").WithName("Writer").WithDataTypeId(writer.Id)
                .WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.MultiNodeTreePicker).WithVariations(ContentVariation.Nothing)
                .Done()
            .Build();
        _page.AllowedAsRoot = true;
        await ContentTypeService.CreateAsync(_page, Constants.Security.SuperUserKey);

        // The framework: generic abilities, the change set service applying inside an Umbraco scope, and the
        // CMS document adapter.
        var abilities = new AbilityRegistry();
        var adapters = new EntityAdapterRegistry();
        var itemTypes = new ItemTypeRegistry();
        VetoHandler.Applied.Clear();
        VetoHandler.SideEffects.Clear();
        _accessor = ChangeSetAccessor.Ambient();
        _changeSets = new ChangeSetService(new InMemoryChangeSetStore(), abilities, adapters, itemTypes, _accessor, GetRequiredService<IEventAggregator>(), GetRequiredService<IBackgroundTaskQueue>(), TimeSpan.Zero);
        _draftSets = draftSetKind == DraftSetKind.Values
            ? new DraftSetService(
                ContentEditingService,
                GetRequiredService<PropertyEditorCollection>(),
                GetRequiredService<IJsonSerializer>(),
                GetRequiredService<ICoreScopeProvider>(),
                GetRequiredService<IEventAggregator>(),
                GetRequiredService<IAuditService>(),
                GetRequiredService<IIdKeyMap>())
            : new BranchDraftSetService(
                new ContentDraftBranchService(
                    ContentEditingService,
                    GetRequiredService<PropertyEditorCollection>(),
                    GetRequiredService<IJsonSerializer>(),
                    GetRequiredService<ICoreScopeProvider>(),
                    GetRequiredService<IEventAggregator>(),
                    GetRequiredService<IIdKeyMap>()),
                ContentEditingService,
                GetRequiredService<PropertyEditorCollection>(),
                GetRequiredService<IJsonSerializer>(),
                GetRequiredService<ICoreScopeProvider>(),
                GetRequiredService<IEventAggregator>(),
                GetRequiredService<IAuditService>(),
                GetRequiredService<IIdKeyMap>());
        // What documents and media share: the property editor handlers and the schema they build.
        var contentSchemas = new CmsContentSchemas(
            ContentTypeService,
            GetRequiredService<IDataTypeService>(),
            GetRequiredService<ILanguageService>(),
            GetRequiredService<IJsonSerializer>(),
            GetRequiredService<PropertyEditorCollection>(),
            itemTypes,
            GetRequiredService<IPropertyEditorSchemaService>(),
            GetRequiredService<PropertyEditorEntityHandlerCollection>(),
            GetRequiredService<IEntityService>(),
            GetRequiredService<IMediaTypeService>(),
            GetRequiredService<IDynamicRootService>());
        var scope = new CmsScopeUnitOfWorkProvider(GetRequiredService<ICoreScopeProvider>());
        _documents = new CmsDocumentAdapter(
            contentSchemas,
            ContentTypeService,
            ContentEditingService,
            GetRequiredService<IEntityService>(),
            _draftSets,
            GetRequiredService<IUserService>(),
            GetRequiredService<IContentPermissionService>(),
            scope,
            new CmsActingUser(() => Constants.Security.SuperUserKey),
            GetRequiredService<IContentBlueprintEditingService>());
        _media = new CmsMediaAdapter(
            contentSchemas,
            GetRequiredService<IMediaTypeService>(),
            GetRequiredService<IMediaEditingService>(),
            GetRequiredService<IEntityService>(),
            GetRequiredService<IUserService>(),
            GetRequiredService<IMediaPermissionService>(),
            scope,
            new CmsActingUser(() => Constants.Security.SuperUserKey));
        adapters.Add(_documents);
        adapters.Add(_media);

        abilities
            .AddFrameworkAbilities(adapters, itemTypes, _changeSets)
            .Add(new FailingOperationAbility())
            .Add(new FlakyOperationAbility());

        _agent = new Agent(abilities, new AbilityInvoker(abilities, _changeSets));
    }

    [Test]
    public async Task A_page_type_only_used_inside_others_cannot_be_created()
    {
        // A composition: neither allowed at the root nor under any type.
        var controls = new ContentTypeBuilder().WithAlias("articleControls").WithName("Article Controls").Build();
        await ContentTypeService.CreateAsync(controls, Constants.Security.SuperUserKey);

        var subtypes = (await _agent.OkAsync("entity_getSchema", """{ "entityType": "document" }""")).GetProperty("subtypes").EnumerateArray()
            .ToDictionary(s => s.GetProperty("alias").GetString()!, s => s.GetProperty("creatable").GetBoolean());
        Assert.That((subtypes["articleControls"], subtypes["landingPage"]), Is.EqualTo((false, true)));

        await _agent.StartChangeSetAsync("Controls");
        var refused = await _agent.FailAsync("entity_create", """{ "entityType": "document", "subtype": "articleControls", "fields": { "name": "Controls" } }""");
        Assert.That(refused.GetProperty("message").GetString(), Does.Contain("can't be created"));
        Assert.That(refused.GetProperty("problems")[0].GetProperty("path").GetString(), Is.EqualTo("subtype"));
    }

    [Test]
    public async Task The_schema_comes_from_the_document_type_and_its_block_editors()
    {
        var subtypes = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document" }""");
        Assert.That(subtypes.GetProperty("subtypes").EnumerateArray().Select(s => s.GetProperty("alias").GetString()), Does.Contain("landingPage"));

        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage" }""");
        var culture = schema.GetProperty("dimensions").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "culture");
        Assert.That(culture.GetProperty("values").EnumerateArray().Select(c => c.GetString()), Is.EquivalentTo(new[] { "en-US", "da-DK" }));
        Assert.That(schema.GetProperty("fields").GetProperty("properties").GetProperty("subtitle").GetProperty("x-variations")[0].GetString(), Is.EqualTo("culture"));

        var fields = schema.GetProperty("fields").GetProperty("properties");
        Assert.That(TypesIn(fields.GetProperty("blocks")), Is.EquivalentTo(new[] { "hero", "textBlock", "cardGroup" }));
        Assert.That(TypesIn(fields.GetProperty("grid")), Is.EquivalentTo(new[] { "twoColumns", "textBlock" }));

        var hero = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage", "itemTypes": ["hero", "twoColumns"] }""");
        // Settings and a grid block's layout are object fields of the block; areas are items fields.
        var heroFields = hero.GetProperty("itemTypes").GetProperty("hero").GetProperty("fields").GetProperty("properties");
        Assert.That(heroFields.GetProperty("settings").GetProperty("properties").TryGetProperty("theme", out _), Is.True);
        var rowFields = hero.GetProperty("itemTypes").GetProperty("twoColumns").GetProperty("fields").GetProperty("properties");
        Assert.That(rowFields.GetProperty("layout").GetProperty("required")[0].GetString(), Is.EqualTo("columnSpan"));
        var areas = rowFields.EnumerateObject().Where(p => p.Value.TryGetProperty("x-item-types", out _)).Select(p => p.Name);
        Assert.That(areas, Is.EquivalentTo(new[] { "left", "right" }));
    }

    [Test]
    public async Task Every_document_and_media_schema_and_block_schema_is_valid_JSON_Schema()
    {
        // Real data types bring Umbraco's own value schemas: the riskiest part to get wrong.
        var (checkedSchemas, problems) = await Umbraco.Abilities.Tests.SchemaMetaschema.CheckEntitySchemasAsync(
            [_documents.EntityType, _media.EntityType],
            arguments => _agent.UiOkAsync("entity_getSchema", arguments));

        Assert.That(checkedSchemas, Is.GreaterThan(2));
        Assert.That(problems, Is.Empty);
    }

    [Test]
    public async Task Each_field_names_its_data_types_editor_for_a_UI_and_a_model_sees_neither()
    {
        // A form or a change set screen shows each field with the editor the backoffice uses, and can load the data
        // type's configuration by its key. The model gets the same fields without them.
        var ui = await _agent.UiOkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage" }""");
        var subtitle = ui.GetProperty("fields").GetProperty("properties").GetProperty("subtitle");
        var dataType = subtitle.GetProperty("x-meta").GetProperty("umbraco.cms.data-type");
        var textbox = await GetRequiredService<IDataTypeService>().GetAsync(dataType.GetProperty("key").GetGuid());
        Assert.That(textbox!.Id, Is.EqualTo(Constants.DataTypes.Textbox));
        Assert.That(dataType.GetProperty("editorAlias").GetString(), Is.EqualTo(Constants.PropertyEditors.Aliases.TextBox));
        Assert.That(textbox.EditorUiAlias, Is.Not.Null.And.Not.Empty);
        Assert.That(subtitle.GetProperty("x-editor").GetProperty("alias").GetString(), Is.EqualTo(textbox.EditorUiAlias));

        // Block properties are mapped the same way, so a block's fields carry their data types too.
        var hero = await _agent.UiOkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage", "itemTypes": ["hero"] }""");
        Assert.That(hero.ToString(), Does.Contain("umbraco.cms.data-type"));

        var model = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage", "itemTypes": ["hero"] }""");
        Assert.That(model.ToString(), Does.Not.Contain("x-editor").And.Not.Contain("x-meta"));
    }

    [Test]
    public async Task A_tree_picker_is_a_list_of_page_references_saved_in_Umbracos_own_format()
    {
        // The schema marks it as references to documents, and says what the data type allows.
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage" }""");
        var writer = schema.GetProperty("fields").GetProperty("properties").GetProperty("writer");
        Assert.That(writer.GetProperty("items").GetProperty("x-references").GetString(), Is.EqualTo("document"));
        Assert.That(writer.GetProperty("description").GetString(), Does.Contain("Only of these types: author.").And.Contain("Only from under 'Authors'.").And.Contain("At most one."));

        // Find lists the pages of that type, with their type, and not the Authors page.
        var authors = await _agent.OkAsync("entity_find", """{ "entityType": "document", "query": "", "subtype": "author" }""");
        Assert.That(authors.EnumerateArray().Select(a => (a.GetProperty("name").GetString(), a.GetProperty("subtype").GetString())), Is.EqualTo(new[] { ("Ann Holm", "author") }));

        // A key that isn't a page is caught before apply.
        await _agent.StartChangeSetAsync("Writer");
        var page = await CreatePageAsync();
        await SetAsync(page, "", "subtitle", "\"By Ann\"", "en-US");
        await SetAsync(page, "", "writer", $"[\"{Guid.NewGuid()}\"]");
        var invalid = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(invalid.GetProperty("entities")[0].GetProperty("problems")[0].GetProperty("errorMessages")[0].GetString(), Does.StartWith("No document with key"));

        // The author's key is saved in the picker's own format, and reads back as the key.
        await SetAsync(page, "", "writer", $"[\"{_ann}\"]");
        var valid = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(valid.GetProperty("valid").GetBoolean(), Is.True, valid.ToString());
        await _agent.OkAsync("changeSet_apply", SetJson());

        Assert.That(ContentService.GetById(page)!.GetValue<string>("writer"), Does.Contain(_ann.ToString("N")));
        var saved = await _agent.OkAsync("entity_get", $$"""{ "entityType": "document", "key": "{{page}}" }""");
        Assert.That(saved.GetProperty("fields").GetProperty("writer").EnumerateArray().Select(k => k.GetGuid()), Is.EqualTo(new[] { _ann }));
    }

    [Test]
    public async Task A_block_list_offers_the_blocks_its_data_type_allows_now()
    {
        await _agent.StartChangeSetAsync("Blocks");
        var page = await CreatePageAsync();
        await AddItemAsync(page, "blocks", "cardGroup", "{}");

        // An editor takes the card group out of the data type's allowed blocks.
        var dataTypes = GetRequiredService<IDataTypeService>();
        var dataType = (await dataTypes.GetAsync(_pageBlocks.Key))!;
        var config = (BlockListConfiguration)dataType.ConfigurationObject!;
        dataType.ConfigurationData = new Dictionary<string, object>
        {
            { "blocks", config.Blocks.Where(b => b.ContentElementTypeKey != ContentTypeService.Get("cardGroup")!.Key).ToArray() },
        };
        await dataTypes.UpdateAsync(dataType, Constants.Security.SuperUserKey);

        // The next call follows it, with no restart: the card group is no longer offered and can't be added.
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage" }""");
        Assert.That(TypesIn(schema.GetProperty("fields").GetProperty("properties").GetProperty("blocks")), Is.EquivalentTo(new[] { "hero", "textBlock" }));
        await _agent.FailAsync("entity_addItem", $$"""{ "entityType": "document", "key": "{{page}}", "fieldPath": "blocks", "itemType": "cardGroup" }""");

        // The card group the page already has still loads, other edits still work, and validation names it.
        await SetAsync(page, "", "subtitle", "\"Still editable\"", "en-US");
        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.False);
        var problem = validation.GetProperty("entities")[0].GetProperty("problems")[0];
        Assert.That(problem.GetProperty("errorMessages")[0].GetString(), Is.EqualTo("Item type 'cardGroup' is not allowed here."));
    }

    [Test]
    public async Task A_page_built_from_text_is_saved_by_Umbraco_with_its_blocks_settings_grid_and_cultures()
    {
        await _agent.StartChangeSetAsync("Summer landing page");
        var page = await BuildSummerPageAsync();

        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.True, validation.ToString());
        Assert.That(ContentService.GetById(page.Key), Is.Null, "nothing is saved before apply");

        await _agent.OkAsync("changeSet_apply", SetJson());

        // What Umbraco stored.
        var content = ContentService.GetById(page.Key)!;
        Assert.Multiple(() =>
        {
            Assert.That(content.GetCultureName("en-US"), Is.EqualTo("Summer"));
            Assert.That(content.GetCultureName("da-DK"), Is.EqualTo("Sommer"));
            Assert.That(content.GetValue<string>("subtitle", "da-DK"), Is.EqualTo("Sol og strand"));
        });

        var blocks = JsonNode.Parse(content.GetValue<string>("blocks")!)!;
        var heroData = blocks["contentData"]!.AsArray().Single(c => c!["key"]!.ToString() == page.Hero.ToString())!;
        Assert.That(heroData["values"]!.AsArray().Select(v => $"{v!["culture"]}:{v["value"]}"), Is.EquivalentTo(new[] { "en-US:Summer sale", "da-DK:Sommerudsalg" }));
        Assert.That(blocks["settingsData"]![0]!["values"]![0]!["value"]!.ToString(), Is.EqualTo("dark"));

        var grid = JsonNode.Parse(content.GetValue<string>("grid")!)!;
        var rowLayout = grid["layout"]![Constants.PropertyEditors.Aliases.BlockGrid]![0]!;
        Assert.That(rowLayout["columnSpan"]!.GetValue<int>(), Is.EqualTo(12));
        Assert.That(rowLayout["areas"]!.AsArray().Single(a => a!["items"]!.AsArray().Count > 0)!["items"]![0]!["contentKey"]!.ToString(), Is.EqualTo(page.AreaText.ToString()));

        // And loading it back through the adapter gives the same entity.
        var loaded = (await _documents.LoadAsync(page.Key))!;
        // A value tree: blocks are keyed items, settings and layout are objects, and a varying value is per culture.
        var hero = loaded.Fields["blocks"]!.AsArray().Single(b => EntityValues.KeyOf(b!.AsObject()) == page.Hero)!;
        Assert.Multiple(() =>
        {
            Assert.That(hero["heading"]!["da-DK"]!.ToString(), Is.EqualTo("Sommerudsalg"));
            Assert.That(hero["settings"]!["theme"]!.ToString(), Is.EqualTo("dark"));
            var row = loaded.Fields["grid"]![0]!;
            Assert.That(row["layout"]!["columnSpan"]!.GetValue<int>(), Is.EqualTo(12));
            Assert.That(EntityValues.KeyOf(row["left"]![0]!.AsObject()), Is.EqualTo(page.AreaText));
        });
    }

    [Test]
    public async Task Umbraco_validation_problems_come_back_on_the_entity_paths_and_cultures()
    {
        await _agent.StartChangeSetAsync("Missing Danish");
        var page = await CreatePageAsync();
        await SetAsync(page, "", "name", "\"Sommer\"", "da-DK");
        await SetAsync(page, "", "subtitle", "\"Sun and sand\"", "en-US");

        // A hero with English values only: Umbraco treats it as not existing in Danish, so it is valid there.
        await AddItemAsync(page, "blocks", "hero", """{ "heading": "Summer sale" }""", "en-US");

        // A hero with no values yet exists in both cultures, and its mandatory heading is missing in both.
        var empty = await AddItemAsync(page, "blocks", "hero", "{}");
        await SetAsync(page, $"{empty}/settings", "theme", "\"light\"");

        // Ask Umbraco directly, skipping the generic schema check, to see its own errors mapped back.
        var working = (await _changeSets.GetWorkingStateAsync(_changeSets.Get(_agent.ChangeSetId!.Value), "document", page))!;
        var problems = (await _documents.ValidateAsync(working)).Select(p => $"{p.Path}|{p.Variant?.GetValueOrDefault("culture")}").ToList();

        Assert.That(problems, Is.EquivalentTo(new[] { "subtitle|da-DK", $"{empty}/heading|en-US", $"{empty}/heading|da-DK" }), string.Join(", ", problems));

        // The generic schema check agrees with Umbraco, including about the English-only hero.
        var generic = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(generic.GetProperty("entities")[0].GetProperty("problems").EnumerateArray().Select(p => $"{p.GetProperty("path").GetString()}|{p.GetProperty("variant").GetProperty("culture").GetString()}"),
            Is.EquivalentTo(new[] { "subtitle|da-DK", $"{empty}/heading|en-US", $"{empty}/heading|da-DK" }));
    }

    [Test]
    public async Task Umbraco_validation_problems_inside_a_block_in_a_block_come_back_on_its_path()
    {
        await _agent.StartChangeSetAsync("Cards");
        var page = await CreatePageAsync();
        await SetAsync(page, "", "subtitle", "\"Sun and sand\"", "en-US");
        var group = await AddItemAsync(page, "blocks", "cardGroup", "{}");
        await AddItemAsync(page, $"{group}/cards", "card", """{ "title": "Fine" }""");
        var empty = await AddItemAsync(page, $"{group}/cards", "card", "{}");

        var working = (await _changeSets.GetWorkingStateAsync(_changeSets.Get(_agent.ChangeSetId!.Value), "document", page))!;
        var problems = (await _documents.ValidateAsync(working)).Select(p => p.Path).ToList();

        Assert.That(problems, Is.EquivalentTo(new[] { $"{empty}/title" }), string.Join(", ", problems));
    }

    [Test]
    public async Task Editing_an_existing_page_keeps_everything_the_edit_did_not_touch()
    {
        await _agent.StartChangeSetAsync("Create");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("changeSet_apply", SetJson());

        await _agent.StartChangeSetAsync("Edit");
        await SetAsync(page.Key, $"blocks/{page.Hero}", "heading", "\"Big summer sale\"", "en-US");
        await AddItemAsync(page.Key, "blocks", "textBlock", """{ "text": "Ends Sunday" }""", "en-US");
        await _agent.OkAsync("changeSet_apply", SetJson());

        var content = ContentService.GetById(page.Key)!;
        var blocks = JsonNode.Parse(content.GetValue<string>("blocks")!)!;
        var heroValues = blocks["contentData"]!.AsArray().Single(c => c!["key"]!.ToString() == page.Hero.ToString())!["values"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(heroValues.Select(v => $"{v!["culture"]}:{v["value"]}"), Is.EquivalentTo(new[] { "en-US:Big summer sale", "da-DK:Sommerudsalg" }));
            Assert.That(blocks["contentData"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(blocks["settingsData"]![0]!["values"]![0]!["value"]!.ToString(), Is.EqualTo("dark"), "the hero's settings survive the edit");
            Assert.That(content.GetValue<string>("subtitle", "da-DK"), Is.EqualTo("Sol og strand"));
            Assert.That(JsonNode.Parse(content.GetValue<string>("grid")!)!["contentData"]!.AsArray(), Has.Count.EqualTo(3), "the grid survives the edit");
        });
    }

    [Test]
    public async Task A_failed_save_rolls_back_every_document_in_the_Umbraco_scope()
    {
        await _agent.StartChangeSetAsync("Two pages");
        var page = await BuildSummerPageAsync();
        var refused = (await _agent.OkAsync("entity_create", """{ "entityType": "document", "subtype": "landingPage", "variant": { "culture": "en-US" }, "fields": { "name": "Refused", "subtitle": "No" } }"""))
            .GetProperty("key").GetGuid();

        var failure = await _agent.FailAsync("changeSet_apply", SetJson());
        Assert.That(failure.GetProperty("message").GetString(), Does.Contain("Nothing was applied").And.Contain("Content failed and was rolled back"));
        Assert.That(ContentService.GetById(page.Key), Is.Null, "the page saved before the refused one was rolled back");
        Assert.That(ContentService.GetById(refused), Is.Null);
    }

    [Test]
    public async Task Documents_and_media_in_one_set_are_applied_together_in_one_Umbraco_scope()
    {
        await CreateBrochureTypeAsync();
        await _agent.StartChangeSetAsync("Launch");
        var page = await BuildSummerPageAsync();
        var media = await CreateBrochureAsync("Summer brochure");

        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.Multiple(() =>
        {
            Assert.That(ContentService.GetById(page.Key), Is.Not.Null);
            Assert.That(GetRequiredService<IMediaService>().GetById(media)?.Name, Is.EqualTo("Summer brochure"));
        });
    }

    [Test]
    public async Task A_refused_document_rolls_back_the_media_saved_with_it()
    {
        await CreateBrochureTypeAsync();
        await _agent.StartChangeSetAsync("Launch");
        var media = await CreateBrochureAsync("Summer brochure");
        var refused = (await _agent.OkAsync("entity_create", """{ "entityType": "document", "subtype": "landingPage", "variant": { "culture": "en-US" }, "fields": { "name": "Refused", "subtitle": "No" } }"""))
            .GetProperty("key").GetGuid();

        var failure = await _agent.FailAsync("changeSet_apply", SetJson());
        Assert.Multiple(() =>
        {
            Assert.That(failure.GetProperty("message").GetString(), Does.Contain("Nothing was applied"));
            Assert.That(ContentService.GetById(refused), Is.Null);
            // Media is saved before the documents' draft set is applied, so it was saved, then rolled back with the scope.
            Assert.That(GetRequiredService<IMediaService>().GetById(media), Is.Null, "the media saved in the same scope was rolled back");
        });
    }

    [Test]
    public async Task A_page_allowed_only_under_a_parent_is_created_under_it_and_reads_back_in_the_set()
    {
        // A post may only be created under a blog, as Clean's articles only go under an article list.
        var post = new ContentTypeBuilder().WithAlias("post").WithName("Post").WithContentVariation(ContentVariation.Nothing).Build();
        await ContentTypeService.CreateAsync(post, Constants.Security.SuperUserKey);
        var blogType = new ContentTypeBuilder().WithAlias("blog").WithName("Blog").WithContentVariation(ContentVariation.Nothing).Build();
        blogType.AllowedAsRoot = true;
        blogType.AllowedContentTypes = [new ContentTypeSort(post.Key, 0, post.Alias)];
        await ContentTypeService.CreateAsync(blogType, Constants.Security.SuperUserKey);
        var blog = (await ContentEditingService.CreateAsync(
            new ContentCreateModel { ContentTypeKey = blogType.Key, Variants = [new VariantModel { Name = "Blog" }] },
            Constants.Security.SuperUserKey)).Result.Content!;

        await _agent.StartChangeSetAsync("A post");
        var created = await _agent.OkAsync("entity_create", $$"""{ "entityType": "document", "subtype": "post", "fields": { "name": "Hello", "parent": "{{blog.Key}}" } }""");
        var key = created.GetProperty("key").GetGuid();

        // Read back in the set before it exists in Umbraco: access is checked on its parent.
        var read = await _agent.OkAsync("entity_get", $$"""{ "entityType": "document", "key": "{{key}}" }""");
        Assert.That(read.GetProperty("fields").GetProperty("parent").GetString(), Is.EqualTo(blog.Key.ToString()));

        // A later edit stages the new page again: it must keep its parent.
        await _agent.OkAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{key}}", "path": "/name", "value": "Hello again" }""");

        await _agent.OkAsync("changeSet_apply", SetJson());
        var saved = ContentService.GetById(key)!;
        Assert.That(saved.ParentId, Is.EqualTo(blog.Id));
        Assert.That(saved.Name, Is.EqualTo("Hello again"));
    }

    [Test]
    public async Task A_page_moved_in_a_change_set_is_under_its_new_parent_in_the_set_and_moves_when_applied()
    {
        var (blogType, postType) = await CreateBlogTypesAsync();
        var news = await CreateLiveAsync(blogType.Key, "News");
        var archive = await CreateLiveAsync(blogType.Key, "Archive");
        var post = await CreateLiveAsync(postType.Key, "Hello", news.Key);

        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "post" }""");
        Assert.That(schema.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()), Is.EqualTo(new[] { "delete", "move" }));

        await _agent.StartChangeSetAsync("Archive a post");
        await _agent.OkAsync("entity_move", $$"""{ "entityType": "document", "key": "{{post.Key}}", "parent": "{{archive.Key}}" }""");

        // Under its new parent in the set; still under the old one live.
        var read = await _agent.OkAsync("entity_get", $$"""{ "entityType": "document", "key": "{{post.Key}}" }""");
        Assert.That(read.GetProperty("fields").GetProperty("parent").GetString(), Is.EqualTo(archive.Key.ToString()));
        Assert.That(ContentService.GetById(post.Key)!.ParentId, Is.EqualTo(news.Id));

        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(post.Key)!.ParentId, Is.EqualTo(archive.Id));
    }

    [Test]
    public async Task A_page_can_be_moved_under_a_parent_created_in_the_same_set()
    {
        var (blogType, postType) = await CreateBlogTypesAsync();
        var news = await CreateLiveAsync(blogType.Key, "News");
        var post = await CreateLiveAsync(postType.Key, "Hello", news.Key);

        await _agent.StartChangeSetAsync("New archive");
        var archive = (await _agent.OkAsync("entity_create", """{ "entityType": "document", "subtype": "blog", "fields": { "name": "Archive" } }""")).GetProperty("key").GetGuid();
        await _agent.OkAsync("entity_move", $$"""{ "entityType": "document", "key": "{{post.Key}}", "parent": "{{archive}}" }""");

        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(post.Key)!.ParentId, Is.EqualTo(ContentService.GetById(archive)!.Id));
    }

    [Test]
    public async Task A_move_Umbraco_would_refuse_is_a_problem_on_the_parent_before_it_is_applied()
    {
        var (blogType, postType) = await CreateBlogTypesAsync();
        var news = await CreateLiveAsync(blogType.Key, "News");
        var post = await CreateLiveAsync(postType.Key, "Hello", news.Key);

        await _agent.StartChangeSetAsync("Bad moves");
        await _agent.OkAsync("entity_move", $$"""{ "entityType": "document", "key": "{{post.Key}}" }""");
        await _agent.OkAsync("entity_move", $$"""{ "entityType": "document", "key": "{{news.Key}}", "parent": "{{post.Key}}" }""");

        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        var problems = validation.GetProperty("entities").EnumerateArray()
            .SelectMany(e => e.GetProperty("problems").EnumerateArray().Select(p => $"{e.GetProperty("entity").GetProperty("name").GetString()}|{p.GetProperty("path").GetString()}|{p.GetProperty("errorMessages")[0].GetString()}"))
            .ToList();
        Assert.That(problems, Is.EquivalentTo(new[]
        {
            "Hello|parent|This document type isn't allowed at the root.",
            "News|parent|A document can't be moved under itself.",
        }), string.Join(", ", problems));
    }

    [Test]
    public async Task A_page_deleted_in_a_change_set_is_gone_in_the_set_and_goes_to_the_recycle_bin_when_applied()
    {
        var (blogType, _) = await CreateBlogTypesAsync();
        var news = await CreateLiveAsync(blogType.Key, "News");

        await _agent.StartChangeSetAsync("Remove the news");
        var deleted = await _agent.OkAsync("entity_delete", $$"""{ "entityType": "document", "key": "{{news.Key}}" }""");
        Assert.That(deleted.GetProperty("canRestore").GetBoolean(), Is.True);

        // Gone in the set, still live outside it.
        await _agent.FailAsync("entity_get", $$"""{ "entityType": "document", "key": "{{news.Key}}" }""");
        Assert.That((await _agent.OkAsync("entity_find", """{ "entityType": "document", "query": "News" }""")).EnumerateArray(), Is.Empty);
        Assert.That(ContentService.GetById(news.Key)!.Trashed, Is.False);

        var summary = await _agent.OkAsync("changeSet_summarize", SetJson());
        Assert.That(summary.GetProperty("entities")[0].GetProperty("change").GetString(), Is.EqualTo("deleted"));

        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(news.Key)!.Trashed, Is.True);
    }

    [Test]
    public async Task A_page_moved_out_of_a_page_deleted_in_the_same_set_is_moved_first_and_not_deleted()
    {
        var (blogType, postType) = await CreateBlogTypesAsync();
        var news = await CreateLiveAsync(blogType.Key, "News");
        var archive = await CreateLiveAsync(blogType.Key, "Archive");
        var post = await CreateLiveAsync(postType.Key, "Hello", news.Key);

        await _agent.StartChangeSetAsync("Close the news");
        await _agent.OkAsync("entity_delete", $$"""{ "entityType": "document", "key": "{{news.Key}}" }""");
        await _agent.OkAsync("entity_move", $$"""{ "entityType": "document", "key": "{{post.Key}}", "parent": "{{archive.Key}}" }""");

        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.Multiple(() =>
        {
            Assert.That(ContentService.GetById(news.Key)!.Trashed, Is.True);
            Assert.That(ContentService.GetById(post.Key)!.Trashed, Is.False);
            Assert.That(ContentService.GetById(post.Key)!.ParentId, Is.EqualTo(archive.Id));
        });
    }

    [Test]
    public async Task A_page_created_and_deleted_in_one_set_is_never_saved()
    {
        await _agent.StartChangeSetAsync("Change of mind");
        var page = await CreatePageAsync();
        await _agent.OkAsync("entity_delete", $$"""{ "entityType": "document", "key": "{{page}}" }""");

        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(page), Is.Null);
    }

    [Test]
    public async Task Moving_and_deleting_take_the_CMS_permissions_for_them_however_the_change_is_made()
    {
        var (blogType, postType) = await CreateBlogTypesAsync();
        var news = await CreateLiveAsync(blogType.Key, "News");
        var archive = await CreateLiveAsync(blogType.Key, "Archive");
        var post = await CreateLiveAsync(postType.Key, "Hello", news.Key);

        // A user who may update pages, but not move or delete them.
        _agent.UserKey = await CreateUserAsync("Updaters", ActionBrowse.ActionLetter, ActionUpdate.ActionLetter);
        await _agent.StartChangeSetAsync("Not allowed");
        var move = await _agent.FailAsync("entity_move", $$"""{ "entityType": "document", "key": "{{post.Key}}", "parent": "{{archive.Key}}" }""");
        var setParent = await _agent.FailAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{post.Key}}", "path": "parent", "value": "{{archive.Key}}" }""");
        var delete = await _agent.FailAsync("entity_delete", $$"""{ "entityType": "document", "key": "{{post.Key}}" }""");
        Assert.Multiple(() =>
        {
            Assert.That(move.GetProperty("message").GetString(), Is.EqualTo("You don't have permission to move this document."));
            Assert.That(setParent.GetProperty("message").GetString(), Is.EqualTo("You don't have permission to move this document."));
            Assert.That(delete.GetProperty("message").GetString(), Is.EqualTo("You don't have permission to delete this document."));
        });
        await _agent.OkAsync("changeSet_discard", "{}");

        // A user who may move pages, but not create under the new parent: refused, as in the backoffice.
        _agent.UserKey = await CreateUserAsync("Movers", ActionBrowse.ActionLetter, ActionMove.ActionLetter);
        await _agent.StartChangeSetAsync("Not there");
        move = await _agent.FailAsync("entity_move", $$"""{ "entityType": "document", "key": "{{post.Key}}", "parent": "{{archive.Key}}" }""");
        Assert.That(move.GetProperty("message").GetString(), Is.EqualTo("You don't have permission to move this document there."));
    }

    [Test]
    public async Task A_page_moved_in_both_the_set_and_the_backoffice_is_a_conflict_on_its_parent()
    {
        var (blogType, postType) = await CreateBlogTypesAsync();
        var news = await CreateLiveAsync(blogType.Key, "News");
        var archive = await CreateLiveAsync(blogType.Key, "Archive");
        var old = await CreateLiveAsync(blogType.Key, "Old");
        var post = await CreateLiveAsync(postType.Key, "Hello", news.Key);

        await _agent.StartChangeSetAsync("Archive a post");
        await _agent.OkAsync("entity_move", $$"""{ "entityType": "document", "key": "{{post.Key}}", "parent": "{{archive.Key}}" }""");

        await Task.Delay(20);
        Assert.That((await ContentEditingService.MoveAsync(post.Key, old.Key, Constants.Security.SuperUserKey)).Success, Is.True);

        var refused = await _agent.FailAsync("changeSet_apply", SetJson());
        Assert.That(refused.GetProperty("problems")[0].GetProperty("path").GetString(), Does.EndWith("/parent"));

        await _agent.OkAsync("changeSet_resolveConflict", $$"""
            { "changeSetId": "{{_agent.ChangeSetId}}", "entityType": "document", "key": "{{post.Key}}", "path": "parent", "keep": "theirs" }
            """);
        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(post.Key)!.ParentId, Is.EqualTo(old.Id));
    }

    /// <summary>Blogs, allowed at the root, hold posts, which aren't.</summary>
    private async Task<(IContentType Blog, IContentType Post)> CreateBlogTypesAsync()
    {
        var post = new ContentTypeBuilder().WithAlias("post").WithName("Post").WithContentVariation(ContentVariation.Nothing).Build();
        await ContentTypeService.CreateAsync(post, Constants.Security.SuperUserKey);
        var blog = new ContentTypeBuilder().WithAlias("blog").WithName("Blog").WithContentVariation(ContentVariation.Nothing).Build();
        blog.AllowedAsRoot = true;
        blog.AllowedContentTypes = [new ContentTypeSort(post.Key, 0, post.Alias)];
        await ContentTypeService.CreateAsync(blog, Constants.Security.SuperUserKey);
        return (blog, post);
    }

    private async Task<IContent> CreateLiveAsync(Guid contentTypeKey, string name, Guid? parent = null)
        => (await ContentEditingService.CreateAsync(
            new ContentCreateModel { ContentTypeKey = contentTypeKey, ParentKey = parent, Variants = [new VariantModel { Name = name }] },
            Constants.Security.SuperUserKey)).Result.Content!;

    private async Task CreateBrochureTypeAsync()
    {
        var brochure = new MediaTypeBuilder().WithAlias("brochure").WithName("Brochure")
            .AddPropertyGroup().WithAlias("content").WithName("Content")
                .AddPropertyType().WithAlias("title").WithName("Title")
                    .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                    .WithValueStorageType(ValueStorageType.Nvarchar).Done()
                .Done()
            .Build();
        brochure.AllowedAsRoot = true;
        await GetRequiredService<IMediaTypeService>().CreateAsync(brochure, Constants.Security.SuperUserKey);
    }

    private async Task<Guid> CreateBrochureAsync(string name)
        => (await _agent.OkAsync("entity_create", $$"""{ "entityType": "media", "subtype": "brochure", "fields": { "name": "{{name}}", "title": "Summer" } }""")).GetProperty("key").GetGuid();

    [Test]
    public async Task A_failed_side_effect_after_the_content_leaves_the_content_applied_and_the_set_resumable()
    {
        await _agent.StartChangeSetAsync("Content, then an operation");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("test_fail", "{}");

        FailingOperationAbility.Fail = true;
        var failure = await _agent.FailAsync("changeSet_apply", SetJson());
        Assert.That(failure.GetProperty("message").GetString(), Does.Contain("Partly applied"));
        Assert.That(ContentService.GetById(page.Key), Is.Not.Null, "Content's step committed before the operation failed");
        var version = ContentService.GetById(page.Key)!.VersionId;

        FailingOperationAbility.Fail = false;
        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(page.Key)!.VersionId, Is.EqualTo(version), "resuming does not save the page again");
    }

    [Test]
    public async Task A_page_deleted_in_the_backoffice_while_a_set_edits_it_is_still_summarized_and_refused_on_apply()
    {
        await _agent.StartChangeSetAsync("Create");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("changeSet_apply", SetJson());

        await _agent.StartChangeSetAsync("Edit");
        await SetAsync(page.Key, "", "subtitle", "\"Sun, sand and sea\"", "en-US");
        ContentService.Delete(ContentService.GetById(page.Key)!);

        var summary = await _agent.OkAsync("changeSet_summarize", SetJson());
        Assert.That(summary.GetProperty("entities")[0].GetProperty("entity").GetProperty("key").GetGuid(), Is.EqualTo(page.Key));
        await _agent.FailAsync("changeSet_apply", SetJson());
    }

    [Test]
    public async Task Changes_saved_in_the_backoffice_meanwhile_are_rebased_onto_when_they_do_not_overlap()
    {
        await _agent.StartChangeSetAsync("Create");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("changeSet_apply", SetJson());

        await _agent.StartChangeSetAsync("Edit");
        await SetAsync(page.Key, $"blocks/{page.Hero}", "heading", "\"Big summer sale\"", "en-US");

        // Someone changes the Danish subtitle in the backoffice meanwhile.
        await Task.Delay(20);
        var content = ContentService.GetById(page.Key)!;
        content.SetValue("subtitle", "Sol, strand og hav", "da-DK");
        ContentService.Save(content);

        var summary = await _agent.OkAsync("changeSet_summarize", SetJson());
        Assert.That(summary.GetProperty("rebased")[0].GetProperty("changedSince").EnumerateArray().Select(c => c.GetString()), Does.Contain("subtitle (da-DK)"));

        await _agent.OkAsync("changeSet_apply", SetJson());
        content = ContentService.GetById(page.Key)!;
        var heroValues = JsonNode.Parse(content.GetValue<string>("blocks")!)!["contentData"]!.AsArray()
            .Single(c => c!["key"]!.ToString() == page.Hero.ToString())!["values"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(content.GetValue<string>("subtitle", "da-DK"), Is.EqualTo("Sol, strand og hav"), "the backoffice change is kept");
            Assert.That(heroValues.Select(v => $"{v!["culture"]}:{v["value"]}"), Does.Contain("en-US:Big summer sale"), "the set's change is applied on top");
        });
    }

    [Test]
    public async Task A_value_changed_in_both_the_set_and_the_backoffice_is_a_conflict_to_resolve()
    {
        await _agent.StartChangeSetAsync("Create");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("changeSet_apply", SetJson());

        await _agent.StartChangeSetAsync("Edit");
        await SetAsync(page.Key, "", "subtitle", "\"Sun, sand and sea\"", "en-US");

        await Task.Delay(20);
        var content = ContentService.GetById(page.Key)!;
        content.SetValue("subtitle", "Changed in the backoffice", "en-US");
        ContentService.Save(content);

        var refused = await _agent.FailAsync("changeSet_apply", SetJson());
        var problem = refused.GetProperty("problems")[0];
        Assert.Multiple(() =>
        {
            Assert.That(problem.GetProperty("path").GetString(), Does.EndWith("/subtitle"));
            Assert.That(problem.GetProperty("variant").GetProperty("culture").GetString(), Is.EqualTo("en-US"));
        });

        await _agent.OkAsync("changeSet_resolveConflict", $$"""
            { "changeSetId": "{{_agent.ChangeSetId}}", "entityType": "document", "key": "{{page.Key}}", "path": "subtitle", "variant": { "culture": "en-US" }, "keep": "theirs" }
            """);
        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(page.Key)!.GetValue<string>("subtitle", "en-US"), Is.EqualTo("Changed in the backoffice"));
    }

    [Test]
    public async Task Content_changes_in_a_change_set_are_a_CMS_draft_set_that_records_only_what_changed()
    {
        await _agent.StartChangeSetAsync("Create");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("changeSet_apply", SetJson());

        await _agent.StartChangeSetAsync("Edit");
        await SetAsync(page.Key, "", "subtitle", "\"Sun, sand and sea\"", "en-US");

        // The change set holds a CMS draft set for content, which the backoffice can read and preview.
        var draftId = _changeSets.Get(_agent.ChangeSetId!.Value).Drafts[CmsDocumentAdapter.Type];
        var draft = _draftSets.Get(draftId)!;
        var inDraft = (await _draftSets.GetAsync(draftId, new GuidUdi(Constants.UdiEntityType.Document, page.Key)))!;
        Assert.Multiple(() =>
        {
            Assert.That(draft.Name, Is.EqualTo("Edit"));
            Assert.That(draft.Entities.Select(e => e.ToString()), Is.EqualTo(new[] { $"umb://document/{page.Key:N}" }));
            Assert.That(inDraft.Values.Single(v => v.Alias == "subtitle" && v.Culture == "en-US").Value!.ToString(), Is.EqualTo("Sun, sand and sea"));
            Assert.That(ContentService.GetById(page.Key)!.GetValue<string>("subtitle", "en-US"), Is.EqualTo("Sun and sand"), "live is untouched");
        });

        // Setting it back is no longer a change.
        await SetAsync(page.Key, "", "subtitle", "\"Sun and sand\"", "en-US");
        await Task.Delay(20);
        var content = ContentService.GetById(page.Key)!;
        content.SetValue("subtitle", "Changed in the backoffice", "en-US");
        ContentService.Save(content);
        var summary = await _agent.OkAsync("changeSet_summarize", SetJson());
        Assert.That(summary.GetProperty("rebased")[0].GetProperty("conflicts").GetArrayLength(), Is.Zero, "the draft set no longer changes the subtitle");

        // Discarding the change set discards the draft set.
        await _agent.OkAsync("changeSet_discard", SetJson());
        Assert.That(_draftSets.Get(draftId), Is.Null);
    }

    [Test]
    public async Task A_draft_set_works_on_its_own_with_no_abilities()
    {
        // Everything here is CMS only: content editing and the draft set, as the backoffice would use them.
        var key = Guid.NewGuid();
        var created = await ContentEditingService.CreateAsync(
            new ContentCreateModel
            {
                Key = key,
                ContentTypeKey = _page.Key,
                Variants = [new VariantModel { Culture = "en-US", Name = "Plain" }],
                Properties = [new PropertyValueModel { Alias = "subtitle", Culture = "en-US", Value = "One" }],
            },
            Constants.Security.SuperUserKey);
        Assert.That(created.Success, Is.True);

        var udi = new GuidUdi(Constants.UdiEntityType.Document, key);
        var draft = _draftSets.Create("Rename");
        await _draftSets.StageChangesAsync(draft.Id, udi,
        [
            new DraftValue(DraftSetService.NameAlias, "en-US", null, JsonValue.Create("Renamed")),
            new DraftValue("subtitle", "en-US", null, JsonValue.Create("Two")),
        ]);
        Assert.That((await _draftSets.GetAsync(draft.Id, udi))!.Names["en-US"], Is.EqualTo("Renamed"));

        // Someone changes the subtitle meanwhile: a conflict. They keep the live value.
        await Task.Delay(20);
        var content = ContentService.GetById(key)!;
        content.SetValue("subtitle", "Three", "en-US");
        ContentService.Save(content);
        var conflict = (await _draftSets.GetRebaseAsync(draft.Id)).Single().Conflicts.Single();
        Assert.That((conflict.Alias, conflict.Draft!.ToString(), conflict.Live!.ToString()), Is.EqualTo(("subtitle", "Two", "Three")));
        await _draftSets.ResolveAsync(draft.Id, udi, "subtitle", "en-US", null, DraftKeep.Live);

        var errors = (await _draftSets.ValidateAsync(draft.Id, Constants.Security.SuperUserKey))[udi];
        Assert.That(errors.Select(e => $"{e.Alias}|{e.Culture}|{e.JsonPath}|{string.Join(" ", e.ErrorMessages)}"), Is.Empty);
        await _draftSets.ApplyAsync(draft.Id, Constants.Security.SuperUserKey);
        content = ContentService.GetById(key)!;
        Assert.Multiple(() =>
        {
            Assert.That(content.GetCultureName("en-US"), Is.EqualTo("Renamed"));
            Assert.That(content.GetValue<string>("subtitle", "en-US"), Is.EqualTo("Three"));
            Assert.That(draft.Applied, Is.True);
        });

        // Applying wrote to the CMS audit log, and raised the applied notification.
        var audit = await GetRequiredService<IAuditService>().GetItemsByKeyAsync(key, UmbracoObjectTypes.Document, 0, 10, Direction.Descending, null, [AuditType.Custom]);
        Assert.That(audit.Items.Select(i => i.Comment), Does.Contain("Applied with draft set 'Rename'"));
        Assert.That(VetoHandler.Applied, Does.Contain("draft set: Rename"));
    }

    [Test]
    public async Task A_package_can_veto_applying_a_change_set_or_its_draft_set_with_a_CMS_notification()
    {
        // Vetoed at the change set: nothing runs.
        await _agent.StartChangeSetAsync("Not today");
        var page = await BuildSummerPageAsync();
        var refused = await _agent.FailAsync("changeSet_apply", SetJson());
        Assert.That(refused.GetProperty("message").GetString(), Is.EqualTo("Applying was refused. Change sets called 'Not today' can't be applied."));
        Assert.That(ContentService.GetById(page.Key), Is.Null);
        await _agent.OkAsync("changeSet_discard", "{}");

        // Vetoed at the CMS draft set: the Content step fails and rolls back.
        await _agent.StartChangeSetAsync("Not this content");
        page = await BuildSummerPageAsync();
        refused = await _agent.FailAsync("changeSet_apply", SetJson());
        Assert.That(refused.GetProperty("message").GetString(), Does.Contain("Content failed and was rolled back").And.Contain("Draft sets called 'Not this content' can't be applied."));
        Assert.That(ContentService.GetById(page.Key), Is.Null);
        await _agent.OkAsync("changeSet_discard", "{}");

        // Applied: both notifications are raised.
        await _agent.StartChangeSetAsync("Fine");
        await BuildSummerPageAsync();
        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(VetoHandler.Applied, Is.EquivalentTo(new[] { "draft set: Fine", "change set: Fine" }));
    }

    [Test]
    public async Task An_agent_gets_the_CMS_permissions_of_the_user_it_acts_for()
    {
        await _agent.StartChangeSetAsync("Create");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("changeSet_apply", SetJson());

        // A user who may browse content but not update it can read the page, and can't change it.
        var reader = await CreateUserAsync("Readers", ActionBrowse.ActionLetter);
        _agent.UserKey = reader;
        await _agent.StartChangeSetAsync("Edit");
        await _agent.OkAsync("entity_get", $$"""{ "entityType": "document", "key": "{{page.Key}}" }""");
        var refused = await _agent.FailAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page.Key}}", "path": "subtitle", "value": "Changed", "variant": { "culture": "en-US" } }""");
        Assert.Multiple(() =>
        {
            Assert.That(refused.GetProperty("status").GetString(), Is.EqualTo("AccessDenied"));
            Assert.That(refused.GetProperty("message").GetString(), Is.EqualTo("You don't have permission to update this document."));
        });
        await _agent.OkAsync("changeSet_discard", "{}");

        // A set made by someone who may change the page, applied by the reader: refused, nothing changes.
        _agent.UserKey = Constants.Security.SuperUserKey;
        await _agent.StartChangeSetAsync("Edit for the reader");
        await SetAsync(page.Key, "", "subtitle", "\"Changed\"", "en-US");
        _agent.UserKey = reader;
        refused = await _agent.FailAsync("changeSet_apply", SetJson());
        Assert.Multiple(() =>
        {
            Assert.That(refused.GetProperty("status").GetString(), Is.EqualTo("AccessDenied"));
            Assert.That(ContentService.GetById(page.Key)!.GetValue<string>("subtitle", "en-US"), Is.EqualTo("Sun and sand"));
        });
    }

    [Test]
    public async Task A_draft_set_applied_inside_a_scope_that_rolls_back_is_not_applied_and_raises_nothing()
    {
        var key = Guid.NewGuid();
        await ContentEditingService.CreateAsync(
            new ContentCreateModel
            {
                Key = key,
                ContentTypeKey = _page.Key,
                Variants = [new VariantModel { Culture = "en-US", Name = "Plain" }],
                Properties = [new PropertyValueModel { Alias = "subtitle", Culture = "en-US", Value = "One" }],
            },
            Constants.Security.SuperUserKey);
        var udi = new GuidUdi(Constants.UdiEntityType.Document, key);
        var draft = _draftSets.Create("Rolled back");
        await _draftSets.StageChangesAsync(draft.Id, udi, [new DraftValue("subtitle", "en-US", null, JsonValue.Create("Two"))]);

        // The draft set's own scope completes, but the outer one doesn't.
        using (GetRequiredService<ICoreScopeProvider>().CreateCoreScope())
        {
            await _draftSets.ApplyAsync(draft.Id, Constants.Security.SuperUserKey);
        }

        Assert.Multiple(() =>
        {
            Assert.That(ContentService.GetById(key)!.GetValue<string>("subtitle", "en-US"), Is.EqualTo("One"));
            Assert.That(draft.Applied, Is.False, "it can still be applied");
            Assert.That(VetoHandler.Applied, Is.Empty, "no applied notification for a rollback");
        });

        await _draftSets.ApplyAsync(draft.Id, Constants.Security.SuperUserKey);
        Assert.That(ContentService.GetById(key)!.GetValue<string>("subtitle", "en-US"), Is.EqualTo("Two"));
        Assert.That(VetoHandler.Applied, Is.EqualTo(new[] { "draft set: Rolled back" }));
    }

    private async Task<Guid> CreateUserAsync(string name, params string[] permissions)
    {
        var group = new UserGroup(GetRequiredService<IShortStringHelper>())
        {
            Alias = name.ToLowerInvariant(),
            Name = name,
            Permissions = permissions.ToHashSet(),
            StartContentId = Constants.System.Root,
            HasAccessToAllLanguages = true,
        };
        group.AddAllowedSection(Constants.Applications.Content);
        Assert.That((await GetRequiredService<IUserGroupService>().CreateAsync(group, Constants.Security.SuperUserKey)).Success, Is.True);

        var created = await GetRequiredService<IUserService>().CreateAsync(
            Constants.Security.SuperUserKey,
            new UserCreateModel { Email = $"{group.Alias}@example.com", UserName = $"{group.Alias}@example.com", Name = name, UserGroupKeys = new HashSet<Guid> { group.Key } },
            approveUser: true);
        Assert.That(created.Success, Is.True, created.Status.ToString());
        return created.Result.CreatedUser!.Key;
    }

    [Test]
    public async Task A_side_effect_with_retries_runs_on_Umbracos_background_queue_after_the_content_commits()
    {
        FlakyOperationAbility.FailuresLeft = 1;
        await _agent.StartChangeSetAsync("Content, then a flaky operation");
        var page = await BuildSummerPageAsync();
        await _agent.OkAsync("test_flaky", "{}");
        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(page.Key), Is.Not.Null);

        // A site runs Umbraco's queued hosted service; the test host doesn't start it, so the test does.
        var processor = new QueuedHostedService(GetRequiredService<IBackgroundTaskQueue>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<QueuedHostedService>.Instance);
        await processor.StartAsync(CancellationToken.None);

        // Wait for the notification.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (VetoHandler.SideEffects.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await processor.StopAsync(CancellationToken.None);
        Assert.That(VetoHandler.SideEffects.TryPeek(out var completed), Is.True, "the background queue never ran the side effect");
        Assert.Multiple(() =>
        {
            Assert.That(completed!.AbilityId, Is.EqualTo("test.flaky"));
            Assert.That(completed.Result.Success, Is.True);
            Assert.That(completed.Attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Editors_the_definition_has_no_rule_for_are_fields_described_by_Umbracos_value_schemas()
    {
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage" }""");
        var fields = schema.GetProperty("fields").GetProperty("properties");
        var accent = fields.GetProperty("accent");
        var launchDate = fields.GetProperty("launchDate");
        var size = fields.GetProperty("size");
        Assert.Multiple(() =>
        {
            Assert.That(accent.TryGetProperty("type", out _), Is.True, $"the eyedropper describes its value: {accent}");
            Assert.That(launchDate.GetProperty("format").GetString(), Is.EqualTo("date-time"));
            Assert.That(size.GetProperty("items").GetProperty("type").GetString(), Is.EqualTo("string"), size.ToString());
        });

        // Set whole, in the editor's own format, and saved by Umbraco.
        await _agent.StartChangeSetAsync("Accent");
        var page = await CreatePageAsync();
        await SetAsync(page, "", "subtitle", "\"Sun and sand\"", "en-US");
        await SetAsync(page, "", "accent", "\"#336699\"");
        await _agent.OkAsync("changeSet_apply", SetJson());
        Assert.That(ContentService.GetById(page)!.GetValue<string>("accent"), Is.EqualTo("#336699"));
    }

    [Test]
    public async Task A_third_party_editor_with_a_handler_is_a_field_shaped_by_that_handler()
    {
        // The package's handler makes the rating a whole number, not the editor's JSON.
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "landingPage" }""");
        var rating = schema.GetProperty("fields").GetProperty("properties").GetProperty("rating");
        Assert.That(rating.GetProperty("type").GetString(), Is.EqualTo("integer"), rating.ToString());

        // Its rules are checked before Umbraco sees the value.
        await _agent.StartChangeSetAsync("Rating");
        var page = await CreatePageAsync();
        await SetAsync(page, "", "subtitle", "\"Sun and sand\"", "en-US");
        await SetAsync(page, "", "rating", "7");
        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        var problem = validation.GetProperty("entities")[0].GetProperty("problems")[0];
        Assert.That(problem.GetProperty("path").GetString(), Is.EqualTo("rating"), problem.ToString());

        // Saved in the editor's own shape, and read back as the number.
        await SetAsync(page, "", "rating", "4");
        await _agent.OkAsync("changeSet_apply", SetJson());
        var stored = JsonNode.Parse(ContentService.GetById(page)!.GetValue<string>("rating")!)!;
        var loaded = (await _documents.LoadAsync(page))!;
        Assert.Multiple(() =>
        {
            Assert.That(stored["stars"]!.GetValue<int>(), Is.EqualTo(4));
            Assert.That(stored["of"]!.GetValue<int>(), Is.EqualTo(5));
            Assert.That(EntityValues.ReadAny(loaded.Fields, "rating")!.GetValue<int>(), Is.EqualTo(4));
        });
    }

    [Test]
    public async Task A_new_page_and_a_new_block_start_with_their_editors_presets()
    {
        // A toggle preset to on, and a number whose lowest allowed value is 3: what the backoffice would start them at.
        var toggle = await CreateConfiguredDataTypeAsync(Constants.PropertyEditors.Aliases.Boolean, "Featured", ValueStorageType.Integer, ("default", true));
        var number = await CreateConfiguredDataTypeAsync(Constants.PropertyEditors.Aliases.Integer, "Priority", ValueStorageType.Integer, ("min", 3));
        var offer = new ContentTypeBuilder().WithAlias("offer").WithName("offer").WithIsElement(true)
            .AddPropertyType().WithAlias("highlight").WithName("Highlight").WithDataTypeId(toggle.Id).WithVariations(ContentVariation.Nothing).Done()
            .Build();
        await ContentTypeService.CreateAsync(offer, Constants.Security.SuperUserKey);
        var offers = await CreateDataTypeAsync(Constants.PropertyEditors.Aliases.BlockList, "Offers", new BlockListConfiguration.BlockConfiguration[] { new() { ContentElementTypeKey = offer.Key } });
        var type = new ContentTypeBuilder().WithAlias("offerPage").WithName("Offer page")
            .AddPropertyType().WithAlias("featured").WithName("Featured").WithDataTypeId(toggle.Id).WithVariations(ContentVariation.Nothing).Done()
            .AddPropertyType().WithAlias("priority").WithName("Priority").WithDataTypeId(number.Id).WithVariations(ContentVariation.Nothing).Done()
            .AddPropertyType().WithAlias("offers").WithName("Offers").WithDataTypeId(offers.Id).WithVariations(ContentVariation.Nothing).Done()
            .Build();
        type.AllowedAsRoot = true;
        await ContentTypeService.CreateAsync(type, Constants.Security.SuperUserKey);

        // The schema tells the model what a value starts at.
        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "document", "subtype": "offerPage" }""");
        var fields = schema.GetProperty("fields").GetProperty("properties");
        Assert.That(fields.GetProperty("featured").GetProperty("default").GetBoolean(), Is.True);

        await _agent.StartChangeSetAsync("Offers");
        var page = (await _agent.OkAsync("entity_create", """{ "entityType": "document", "subtype": "offerPage", "fields": { "name": "Deals" } }""")).GetProperty("key").GetGuid();
        await AddItemAsync(page, "offers", "offer", "{}");
        await _agent.OkAsync("changeSet_apply", SetJson());

        var content = ContentService.GetById(page)!;
        var block = JsonNode.Parse(content.GetValue<string>("offers")!)!["contentData"]![0]!["values"]![0]!;
        Assert.Multiple(() =>
        {
            Assert.That(content.GetValue<int>("featured"), Is.EqualTo(1));
            Assert.That(content.GetValue<int>("priority"), Is.EqualTo(3));
            Assert.That($"{block["alias"]}:{block["value"]}", Is.EqualTo("highlight:1"), block.ToJsonString());
        });
    }

    [Test]
    public async Task Media_is_built_on_the_same_base_with_the_same_editors_blocks_and_defaults()
    {
        // A media type with a text, a preset toggle and the page's block list: the same handlers as documents.
        var toggle = await CreateConfiguredDataTypeAsync(Constants.PropertyEditors.Aliases.Boolean, "Media featured", ValueStorageType.Integer, ("default", true));
        var brochure = new MediaTypeBuilder().WithAlias("brochure").WithName("Brochure")
            .AddPropertyGroup().WithAlias("content").WithName("Content")
                .AddPropertyType().WithAlias("title").WithName("Title")
                    .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                    .WithValueStorageType(ValueStorageType.Nvarchar).WithMandatory(true).Done()
                .AddPropertyType().WithAlias("featured").WithName("Featured").WithDataTypeId(toggle.Id)
                    .WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.Boolean).WithValueStorageType(ValueStorageType.Integer).Done()
                .AddPropertyType().WithAlias("sections").WithName("Sections").WithDataTypeId(_pageBlocks.Id)
                    .WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.BlockList).WithValueStorageType(ValueStorageType.Ntext).Done()
                .Done()
            .Build();
        brochure.AllowedAsRoot = true;
        await GetRequiredService<IMediaTypeService>().CreateAsync(brochure, Constants.Security.SuperUserKey);

        var schema = await _agent.OkAsync("entity_getSchema", """{ "entityType": "media", "subtype": "brochure" }""");
        var fields = schema.GetProperty("fields").GetProperty("properties");
        Assert.Multiple(() =>
        {
            Assert.That(fields.GetProperty("featured").GetProperty("default").GetBoolean(), Is.True);
            Assert.That(TypesIn(fields.GetProperty("sections")), Does.Contain("cardGroup"));
        });

        // A brochure with a card group whose card has no title: the problem is on the card, inside the block.
        await _agent.StartChangeSetAsync("Brochure");
        var media = (await _agent.OkAsync("entity_create", """{ "entityType": "media", "subtype": "brochure", "fields": { "name": "Summer brochure", "title": "Summer" } }""")).GetProperty("key").GetGuid();
        var group = (await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "media", "key": "{{media}}", "fieldPath": "sections", "itemType": "cardGroup" }""")).GetProperty("path").GetString()!;
        var card = (await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "media", "key": "{{media}}", "fieldPath": "{{group}}/cards", "itemType": "card" }""")).GetProperty("path").GetString()!;
        var stored = (await _changeSets.GetWorkingStateAsync(_changeSets.Get(_agent.ChangeSetId!.Value), "media", media))!;
        var problems = (await _media.ValidateAsync(stored)).Select(p => p.Path).ToList();
        Assert.That(problems, Is.EquivalentTo(new[] { $"{card}/title" }), string.Join(", ", problems));

        // Fixed and applied: saved by Umbraco's media service, and read back the same way.
        await _agent.OkAsync("entity_setValue", $$"""{ "entityType": "media", "key": "{{media}}", "path": "{{card}}/title", "value": "Beach" }""");
        await _agent.OkAsync("changeSet_apply", SetJson());

        var saved = GetRequiredService<IMediaService>().GetById(media)!;
        var loaded = (await _media.LoadAsync(media))!;
        var cards = loaded.Fields["sections"]![0]!["cards"]!;
        Assert.Multiple(() =>
        {
            Assert.That(saved.Name, Is.EqualTo("Summer brochure"));
            Assert.That(saved.GetValue<string>("title"), Is.EqualTo("Summer"));
            Assert.That(saved.GetValue<int>("featured"), Is.EqualTo(1));
            Assert.That(cards[0]!["title"]!.GetValue<string>(), Is.EqualTo("Beach"), loaded.Fields.ToJsonString());
        });
    }

    [Test]
    public async Task Values_that_vary_by_culture_and_segment_are_saved_per_segment_and_load_back()
    {
        await CreateSegmentedPageTypeAsync();
        await _agent.StartChangeSetAsync("Segments");
        var page = await CreateSegmentedPageAsync();
        await SetInAsync(page, "heading", "\"Summer\"", "en-US", null);
        await SetInAsync(page, "heading", "\"Summer, for our VIPs\"", "en-US", "vip");
        await SetInAsync(page, "heading", "\"Summer, for new visitors\"", "en-US", "new");

        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.True, validation.ToString());
        await _agent.OkAsync("changeSet_apply", SetJson());

        var content = ContentService.GetById(page)!;
        Assert.Multiple(() =>
        {
            Assert.That(content.GetValue<string>("heading", "en-US"), Is.EqualTo("Summer"));
            Assert.That(content.GetValue<string>("heading", "en-US", "vip"), Is.EqualTo("Summer, for our VIPs"));
            Assert.That(content.GetValue<string>("heading", "en-US", "new"), Is.EqualTo("Summer, for new visitors"));
        });

        var loaded = (await _documents.LoadAsync(page))!;
        Assert.That(loaded.Fields["heading"]!.AsObject().Select(v => $"{v.Key}:{v.Value}"),
            Is.EquivalentTo(new[] { "en-US:Summer", "en-US|vip:Summer, for our VIPs", "en-US|new:Summer, for new visitors" }));
    }

    [Test]
    public async Task Block_values_that_vary_by_segment_are_saved_in_the_block_and_load_back()
    {
        await CreateSegmentedPageTypeAsync();
        await _agent.StartChangeSetAsync("Segmented blocks");
        var page = await CreateSegmentedPageAsync();
        await SetInAsync(page, "heading", "\"Summer\"", "en-US", null);
        var promo = await AddItemAsync(page, "promos", "promo", """{ "text": "Sale" }""", "en-US");
        await SetInAsync(page, $"{promo}/text", "\"Early access for VIPs\"", "en-US", "vip");

        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.True, validation.ToString());
        await _agent.OkAsync("changeSet_apply", SetJson());

        var promos = JsonNode.Parse(ContentService.GetById(page)!.GetValue<string>("promos")!)!;
        Assert.That(promos["contentData"]![0]!["values"]!.AsArray().Select(v => $"{v!["culture"]}|{v["segment"]}:{v["value"]}"),
            Is.EquivalentTo(new[] { "en-US|:Sale", "en-US|vip:Early access for VIPs" }));

        var loaded = (await _documents.LoadAsync(page))!;
        Assert.That(loaded.Fields["promos"]![0]!["text"]!.AsObject().Select(v => $"{v.Key}:{v.Value}"),
            Is.EquivalentTo(new[] { "en-US:Sale", "en-US|vip:Early access for VIPs" }));
    }

    [Test]
    public async Task A_required_value_missing_in_a_segment_is_a_problem_in_that_segment_as_Umbraco_reports_it()
    {
        // Umbraco validates a culture-and-segment property in each segment the culture has values in
        // (release-18.0.0: ContentValidationServiceBase.HandlePropertiesValidationAsync). An empty segment value
        // doesn't fall back to the default segment's.
        await CreateSegmentedPageTypeAsync();
        await _agent.StartChangeSetAsync("Missing VIP heading");
        var page = await CreateSegmentedPageAsync();
        await SetInAsync(page, "heading", "\"Summer\"", "en-US", null);
        await SetInAsync(page, "teaser", "\"Only for VIPs\"", "en-US", "vip");

        var working = (await _changeSets.GetWorkingStateAsync(_changeSets.Get(_agent.ChangeSetId!.Value), "document", page))!;
        var umbraco = (await _documents.ValidateAsync(working)).Select(p => $"{p.Path}|{p.Variant?.GetValueOrDefault("culture")}|{p.Variant?.GetValueOrDefault("segment")}").ToList();
        Assert.That(umbraco, Is.EquivalentTo(new[] { "heading|en-US|vip" }), string.Join(", ", umbraco));

        var generic = await _agent.OkAsync("changeSet_validate", SetJson());
        var problems = generic.GetProperty("entities")[0].GetProperty("problems").EnumerateArray()
            .Select(p => $"{p.GetProperty("path").GetString()}|{p.GetProperty("variant").GetProperty("culture").GetString()}|{p.GetProperty("variant").GetProperty("segment").GetString()}");
        Assert.That(problems, Is.EquivalentTo(new[] { "heading|en-US|vip" }));
    }

    [Test]
    public async Task A_value_that_varies_by_segment_only_is_shared_by_every_culture_and_saved_per_segment()
    {
        await CreateSegmentedPageTypeAsync(withBanner: true);
        await _agent.StartChangeSetAsync("Segment-only banner");
        var page = await CreateSegmentedPageAsync();
        await SetAsync(page, "", "name", "\"Sommer\"", "da-DK");
        await SetInAsync(page, "heading", "\"Summer\"", "en-US", null);
        await SetInAsync(page, "heading", "\"Sommer\"", "da-DK", null);
        await _agent.OkAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "banner", "value": "Free shipping" }""");
        await _agent.OkAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "banner", "value": "Free shipping and gifts", "variant": { "segment": "vip" } }""");

        var validation = await _agent.OkAsync("changeSet_validate", SetJson());
        Assert.That(validation.GetProperty("valid").GetBoolean(), Is.True, validation.ToString());
        await _agent.OkAsync("changeSet_apply", SetJson());

        var content = ContentService.GetById(page)!;
        Assert.Multiple(() =>
        {
            Assert.That(content.GetValue<string>("banner"), Is.EqualTo("Free shipping"));
            Assert.That(content.GetValue<string>("banner", segment: "vip"), Is.EqualTo("Free shipping and gifts"));
        });

        var loaded = (await _documents.LoadAsync(page))!;
        Assert.That(loaded.Fields["banner"]!.AsObject().Select(v => $"{v.Key}:{v.Value}"),
            Is.EquivalentTo(new[] { ":Free shipping", "vip:Free shipping and gifts" }));
    }

    [Test]
    public async Task A_required_segment_only_value_missing_in_a_segment_is_a_problem_with_no_culture()
    {
        await CreateSegmentedPageTypeAsync(withBanner: true);
        await _agent.StartChangeSetAsync("Missing VIP banner");
        var page = await CreateSegmentedPageAsync();
        await SetInAsync(page, "heading", "\"Summer\"", "en-US", null);
        await SetInAsync(page, "heading", "\"Summer, for our VIPs\"", "en-US", "vip");
        await _agent.OkAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "banner", "value": "Free shipping" }""");

        var working = (await _changeSets.GetWorkingStateAsync(_changeSets.Get(_agent.ChangeSetId!.Value), "document", page))!;
        var umbraco = (await _documents.ValidateAsync(working)).Select(p => $"{p.Path}|{p.Variant?.GetValueOrDefault("culture")}|{p.Variant?.GetValueOrDefault("segment")}").ToList();
        Assert.That(umbraco, Is.EquivalentTo(new[] { "banner||vip" }), string.Join(", ", umbraco));

        var generic = await _agent.OkAsync("changeSet_validate", SetJson());
        var problems = generic.GetProperty("entities")[0].GetProperty("problems").EnumerateArray()
            .Select(p => $"{p.GetProperty("path").GetString()}|{(p.GetProperty("variant").TryGetProperty("culture", out var c) ? c.GetString() : null)}|{p.GetProperty("variant").GetProperty("segment").GetString()}");
        Assert.That(problems, Is.EquivalentTo(new[] { "banner||vip" }));
    }

    /// <summary>
    /// A page type that varies by culture and segment, with a block list of promos whose text does too. With a banner, it
    /// also has a required value that varies by segment only: one per segment, shared by every culture.
    /// </summary>
    private async Task CreateSegmentedPageTypeAsync(bool withBanner = false)
    {
        var promo = await CreateElementTypeAsync("promo", ContentVariation.CultureAndSegment, ("text", ContentVariation.CultureAndSegment, false));
        var promos = await CreateDataTypeAsync(Constants.PropertyEditors.Aliases.BlockList, "Promos", new BlockListConfiguration.BlockConfiguration[] { new() { ContentElementTypeKey = promo.Key } });

        var type = new ContentTypeBuilder()
            .WithAlias("segmentedPage").WithName("Segmented page")
            .WithContentVariation(ContentVariation.CultureAndSegment)
            .AddPropertyType()
                .WithAlias("heading").WithName("Heading")
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(ContentVariation.CultureAndSegment).WithMandatory(true)
                .Done()
            .AddPropertyType()
                .WithAlias("teaser").WithName("Teaser")
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(ContentVariation.CultureAndSegment)
                .Done()
            .AddPropertyType()
                .WithAlias("promos").WithName("Promos").WithDataTypeId(promos.Id).WithVariations(ContentVariation.Nothing)
                .Done()
            .Build();
        if (withBanner)
        {
            type.AddPropertyType(new PropertyTypeBuilder()
                .WithAlias("banner").WithName("Banner")
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(ContentVariation.Segment).WithMandatory(true)
                .Build());
        }

        type.AllowedAsRoot = true;
        await ContentTypeService.CreateAsync(type, Constants.Security.SuperUserKey);
    }

    private async Task<Guid> CreateSegmentedPageAsync()
        => (await _agent.OkAsync("entity_create", """{ "entityType": "document", "subtype": "segmentedPage", "variant": { "culture": "en-US" }, "fields": { "name": "Summer" } }"""))
            .GetProperty("key").GetGuid();

    private Task<JsonElement> SetInAsync(Guid page, string path, string valueJson, string culture, string? segment)
        => _agent.OkAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "{{path}}", "value": {{valueJson}}, "variant": { "culture": "{{culture}}"{{(segment is null ? "" : $", \"segment\": \"{segment}\"")}} } }""");

    private sealed record SummerPage(Guid Key, Guid Hero, Guid AreaText);

    private async Task<SummerPage> BuildSummerPageAsync()
    {
        var page = await CreatePageAsync();
        await SetAsync(page, "", "name", "\"Sommer\"", "da-DK");
        await SetAsync(page, "", "subtitle", "\"Sun and sand\"", "en-US");
        await SetAsync(page, "", "subtitle", "\"Sol og strand\"", "da-DK");

        var hero = await AddItemAsync(page, "blocks", "hero", """{ "heading": "Summer sale" }""", "en-US");
        await SetAsync(page, hero, "heading", "\"Sommerudsalg\"", "da-DK");
        await SetAsync(page, $"{hero}/settings", "theme", "\"dark\"");

        var row = await AddItemAsync(page, "grid", "twoColumns", """{ "label": "Intro row" }""");
        await SetAsync(page, $"{row}/layout", "columnSpan", "12");
        var areaText = await AddItemAsync(page, $"{row}/left", "textBlock", """{ "text": "Left column" }""", "en-US");
        await SetAsync(page, $"{areaText}/layout", "columnSpan", "6");
        var rootText = await AddItemAsync(page, "grid", "textBlock", """{ "text": "Below the row" }""", "en-US");
        await SetAsync(page, $"{rootText}/layout", "columnSpan", "12");

        return new SummerPage(page, Guid.Parse(hero.Split('/')[^1]), Guid.Parse(areaText.Split('/')[^1]));
    }

    private async Task<Guid> CreatePageAsync()
        => (await _agent.OkAsync("entity_create", """{ "entityType": "document", "subtype": "landingPage", "variant": { "culture": "en-US" }, "fields": { "name": "Summer" } }"""))
            .GetProperty("key").GetGuid();

    private Task<JsonElement> SetAsync(Guid page, string elementPath, string field, string valueJson, string? culture = null)
        => _agent.OkAsync("entity_setValue", $$"""{ "entityType": "document", "key": "{{page}}", "path": "{{elementPath}}/{{field}}", "value": {{valueJson}}{{Culture(culture)}} }""");

    private async Task<string> AddItemAsync(Guid page, string fieldPath, string itemType, string fieldsJson, string? culture = null)
        => (await _agent.OkAsync("entity_addItem", $$"""{ "entityType": "document", "key": "{{page}}", "fieldPath": "{{fieldPath}}", "itemType": "{{itemType}}", "fields": {{fieldsJson}}{{Culture(culture)}} }"""))
            .GetProperty("path").GetString()!;

    [Test]
    public async Task A_request_naming_a_change_set_in_its_header_works_in_that_set_for_that_request_only()
    {
        var set = _changeSets.Create("From MCP");
        Guid? seen = null;
        var middleware = new ChangeSetHeaderMiddleware(_ => { seen = _accessor.CurrentChangeSetId; return Task.CompletedTask; });
        var request = new DefaultHttpContext();
        request.Request.Headers[ChangeSetAccessor.HeaderName] = set.Id.ToString();

        await middleware.InvokeAsync(request, _accessor, _changeSets);

        Assert.That(seen, Is.EqualTo(set.Id));
        Assert.That(_accessor.CurrentChangeSetId, Is.Null);
    }

    [Test]
    public async Task A_request_naming_an_unknown_change_set_is_refused_before_it_runs()
    {
        var ran = false;
        var middleware = new ChangeSetHeaderMiddleware(_ => { ran = true; return Task.CompletedTask; });
        var request = new DefaultHttpContext();
        request.Request.Headers[ChangeSetAccessor.HeaderName] = Guid.NewGuid().ToString();

        await middleware.InvokeAsync(request, _accessor, _changeSets);

        Assert.That(ran, Is.False);
        Assert.That(request.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task A_request_without_the_header_works_in_no_change_set()
    {
        Guid? seen = Guid.NewGuid();
        var middleware = new ChangeSetHeaderMiddleware(_ => { seen = _accessor.CurrentChangeSetId; return Task.CompletedTask; });

        await middleware.InvokeAsync(new DefaultHttpContext(), _accessor, _changeSets);

        Assert.That(seen, Is.Null);
    }

    private string SetJson() => $$"""{ "changeSetId": "{{_agent.ChangeSetId}}" }""";

    private static string Culture(string? culture) => culture is null ? string.Empty : $", \"variant\": {{ \"culture\": \"{culture}\" }}";

    private static IEnumerable<string?> TypesIn(JsonElement field) => field.GetProperty("x-item-types").EnumerateArray().Select(t => t.GetProperty("alias").GetString());

    private async Task<IContentType> CreateElementTypeAsync(string alias, ContentVariation variation, params (string Alias, ContentVariation ContentVariation, bool Mandatory)[] properties)
    {
        var builder = new ContentTypeBuilder().WithAlias(alias).WithName(alias).WithIsElement(true).WithContentVariation(variation);
        foreach (var property in properties)
        {
            builder.AddPropertyType()
                .WithAlias(property.Alias).WithName(property.Alias)
                .WithDataTypeId(Constants.DataTypes.Textbox).WithPropertyEditorAlias(Constants.PropertyEditors.Aliases.TextBox)
                .WithValueStorageType(ValueStorageType.Nvarchar).WithVariations(property.ContentVariation).WithMandatory(property.Mandatory)
                .Done();
        }

        var elementType = builder.Build();
        await ContentTypeService.CreateAsync(elementType, Constants.Security.SuperUserKey);
        return elementType;
    }

    private async Task<IDataType> CreateConfiguredDataTypeAsync(string editorAlias, string name, ValueStorageType storage, params (string Alias, object Value)[] settings)
    {
        var dataType = new DataType(GetRequiredService<PropertyEditorCollection>()[editorAlias], GetRequiredService<IConfigurationEditorJsonSerializer>())
        {
            ConfigurationData = settings.ToDictionary(s => s.Alias, s => s.Value),
            Name = name,
            DatabaseType = storage,
            ParentId = Constants.System.Root,
            CreateDate = DateTime.UtcNow,
        };

        await GetRequiredService<IDataTypeService>().CreateAsync(dataType, Constants.Security.SuperUserKey);
        return dataType;
    }

    private async Task<IDataType> CreateEditorDataTypeAsync(string editorAlias, string name)
    {
        var dataType = new DataType(GetRequiredService<PropertyEditorCollection>()[editorAlias], GetRequiredService<IConfigurationEditorJsonSerializer>())
        {
            Name = name,
            DatabaseType = ValueStorageType.Nvarchar,
            ParentId = Constants.System.Root,
            CreateDate = DateTime.UtcNow,
        };

        await GetRequiredService<IDataTypeService>().CreateAsync(dataType, Constants.Security.SuperUserKey);
        return dataType;
    }

    // A tree picker data type that allows one page of type "author", from under an "Authors" page. Makes both types,
    // the Authors page, and one author in it (_ann).
    private async Task<IDataType> CreateTreePickerAsync(string name)
    {
        var author = new ContentTypeBuilder().WithAlias("author").WithName("Author").WithContentVariation(ContentVariation.Nothing).Build();
        var authorList = new ContentTypeBuilder().WithAlias("authorList").WithName("Author list").WithContentVariation(ContentVariation.Nothing).Build();
        authorList.AllowedAsRoot = true;
        await ContentTypeService.CreateAsync(author, Constants.Security.SuperUserKey);
        authorList.AllowedContentTypes = [new ContentTypeSort(author.Key, 0, author.Alias)];
        await ContentTypeService.CreateAsync(authorList, Constants.Security.SuperUserKey);

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
            Name = name,
            DatabaseType = ValueStorageType.Ntext,
            ParentId = Constants.System.Root,
            CreateDate = DateTime.UtcNow,
        };

        await GetRequiredService<IDataTypeService>().CreateAsync(dataType, Constants.Security.SuperUserKey);
        return dataType;
    }

    private async Task<IDataType> CreateDataTypeAsync<T>(string editorAlias, string name, T blocks)
    {
        var dataType = new DataType(GetRequiredService<PropertyEditorCollection>()[editorAlias], GetRequiredService<IConfigurationEditorJsonSerializer>())
        {
            ConfigurationData = new Dictionary<string, object> { { "blocks", blocks! } },
            Name = name,
            DatabaseType = ValueStorageType.Ntext,
            ParentId = Constants.System.Root,
            CreateDate = DateTime.UtcNow,
        };

        await GetRequiredService<IDataTypeService>().CreateAsync(dataType, Constants.Security.SuperUserKey);
        return dataType;
    }

    /// <summary>A recorded operation that fails when the set is applied while <see cref="Fail"/> is set.</summary>
    [Ability("test.fail", "Fail", Kind = AbilityKind.Write, Description = "Fails when run while failing is switched on.")]
    private sealed class FailingOperationAbility : AbilityBase<JsonObject, object>
    {
        public static bool Fail { get; set; }

        protected override Task<AbilityResult> ExecuteAsync(JsonObject input, AbilityContext context, CancellationToken cancellationToken)
            => Fail ? throw new InvalidOperationException("The operation failed on purpose.") : Task.FromResult(Success(new { ran = true }));
    }

    /// <summary>Fails its first <see cref="FailuresLeft"/> runs, then succeeds: a side effect worth retrying.</summary>
    [Ability("test.flaky", "Flaky", Kind = AbilityKind.Write, Retries = 2, Description = "Fails a few times, then works.")]
    private sealed class FlakyOperationAbility : AbilityBase<JsonObject, object>
    {
        public static int FailuresLeft { get; set; }

        protected override Task<AbilityResult> ExecuteAsync(JsonObject input, AbilityContext context, CancellationToken cancellationToken)
            => FailuresLeft-- > 0 ? throw new InvalidOperationException("Not yet.") : Task.FromResult(Success(new { ran = true }));
    }

    private sealed class RefuseSavingHandler : INotificationHandler<ContentSavingNotification>
    {
        public void Handle(ContentSavingNotification notification)
        {
            if (notification.SavedEntities.Any(c => c.Name == "Refused" || c.GetCultureName("en-US") == "Refused"))
            {
                notification.CancelOperation(new EventMessage("Test", "Pages called Refused can't be saved.", EventMessageType.Error));
            }
        }
    }

    private sealed class VetoHandler :
        INotificationHandler<ChangeSetApplyingNotification>,
        INotificationHandler<DraftSetApplyingNotification>,
        INotificationHandler<ChangeSetAppliedNotification>,
        INotificationHandler<DraftSetAppliedNotification>,
        INotificationHandler<ChangeSetSideEffectCompletedNotification>
    {
        public static System.Collections.Concurrent.ConcurrentQueue<ChangeSetSideEffectCompletedNotification> SideEffects { get; } = new();

        public void Handle(ChangeSetSideEffectCompletedNotification notification) => SideEffects.Enqueue(notification);

        public static List<string> Applied { get; } = [];

        public void Handle(ChangeSetApplyingNotification notification)
        {
            if (notification.ChangeSet.Name == "Not today")
            {
                notification.CancelOperation(new EventMessage("Test", "Change sets called 'Not today' can't be applied.", EventMessageType.Error));
            }
        }

        public void Handle(DraftSetApplyingNotification notification)
        {
            if (notification.DraftSet.Name == "Not this content")
            {
                notification.CancelOperation(new EventMessage("Test", "Draft sets called 'Not this content' can't be applied.", EventMessageType.Error));
            }
        }

        public void Handle(ChangeSetAppliedNotification notification) => Applied.Add($"change set: {notification.ChangeSet.Name}");

        public void Handle(DraftSetAppliedNotification notification) => Applied.Add($"draft set: {notification.DraftSet.Name}");
    }

    /// <summary>Calls the AI tools by name with JSON arguments, as a model would.</summary>
    private sealed class Agent
    {
        private readonly Dictionary<string, AIFunction> _tools;

        private readonly AbilityToolSession _session = new(Constants.Security.SuperUserKey);

        private readonly Dictionary<string, AbilityTool> _uiTools;

        private readonly AbilityToolRunner _runner;

        public Agent(AbilityRegistry abilities, AbilityInvoker invoker)
        {
            _tools = AbilityAITools.Create(abilities, invoker, _session).ToDictionary(t => t.Name);
            _uiTools = abilities.All.Select(a => new AbilityTool(a).WithChangeSetIdArgument()).ToDictionary(t => t.Name);
            _runner = new AbilityToolRunner(invoker);
        }

        /// <summary>The conversation's current change set: the only one open in it.</summary>
        public Guid? ChangeSetId => _session.ChangeSetId;

        /// <summary>The user the agent acts for. It gets their permissions, no more.</summary>
        public Guid UserKey
        {
            get => _session.UserKey;
            set => _session.UserKey = value;
        }

        public async Task StartChangeSetAsync(string name)
            => await OkAsync("changeSet_create", $$"""{ "name": "{{name}}" }""");

        /// <summary>
        /// Calls an ability as a UI would, through the Management API: the same result, with the keywords meant for
        /// UIs (x-editor, x-meta) left in.
        /// </summary>
        public async Task<JsonElement> UiOkAsync(string tool, string json)
        {
            var result = (await _runner.InvokeAsync(_uiTools[tool], JsonNode.Parse(json)!.AsObject(), _session, AbilityConsent.Given)).ToJson();
            Assert.That(result.GetProperty("success").GetBoolean(), Is.True, $"{tool} failed: {result}");
            return result.GetProperty("result");
        }

        public async Task<JsonElement> OkAsync(string tool, string json)
        {
            var result = await CallAsync(tool, json);
            Assert.That(result.GetProperty("success").GetBoolean(), Is.True, $"{tool} failed: {result}");
            return result.GetProperty("result");
        }

        public async Task<JsonElement> FailAsync(string tool, string json)
        {
            var result = await CallAsync(tool, json);
            Assert.That(result.GetProperty("success").GetBoolean(), Is.False, $"{tool} should have failed: {result}");
            return result;
        }

        private async Task<JsonElement> CallAsync(string tool, string json)
        {
            using var document = JsonDocument.Parse(json);
            var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
            return (JsonElement)(await _tools[tool].InvokeAsync(arguments))!;
        }
    }
}
