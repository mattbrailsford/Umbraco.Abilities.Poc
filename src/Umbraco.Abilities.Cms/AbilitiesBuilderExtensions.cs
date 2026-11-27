using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Umbraco.Abilities.ChangeSets;
using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Cms.PropertyEditors;
using Umbraco.Abilities.Entities;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.HostedServices;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.DraftSets;

namespace Umbraco.Abilities.Cms;

/// <summary>Registers the abilities framework on a real Umbraco site, with CMS documents and media as its first entity types.</summary>
public static class AbilitiesBuilderExtensions
{
    /// <summary>
    /// Registers the framework as singletons: the ability, adapter and item type registries, the change set service
    /// (change sets kept in memory), the invoker, the tool runner, the filter, policy and consumer registries with the
    /// tool provider, and the document and media adapters. Saves run as the signed-in backoffice user
    /// (<see cref="CmsActingUser"/>). A package adds its own adapters with <c>builder.EntityAdapters()</c> and its own
    /// abilities with <c>builder.Abilities()</c>, its filters with <c>builder.AbilityFilters()</c> and its consumers with
    /// <c>builder.AbilityConsumers()</c>, in any order.
    /// </summary>
    public static IUmbracoBuilder AddAbilities(this IUmbracoBuilder builder)
    {
        var services = builder.Services;

        // The property editor handlers the content schemas use. A site's composer adds the packages' handlers to it.
        builder.PropertyEditorEntityHandlers();

        services.AddSingleton<IChangeSetStore, InMemoryChangeSetStore>();
        services.AddSingleton<ItemTypeRegistry>();
        services.AddSingleton<IChangeSetAccessor, ChangeSetAccessor>();
        services.AddSingleton<DraftSetService>();
        services.AddSingleton<IDraftSetService>(sp => sp.GetRequiredService<DraftSetService>());

        // The same draft sets on named draft branches (a stand-in for CMS PR #24784). Register it as IDraftSetService
        // instead to try change sets on branches.
        services.AddSingleton<ContentDraftBranchService>();
        services.AddSingleton<BranchDraftSetService>();
        services.AddSingleton<CmsScopeUnitOfWorkProvider>();
        services.AddSingleton<CmsContentSchemas>();
        services.AddSingleton(sp => new CmsActingUser(sp.GetRequiredService<IBackOfficeSecurityAccessor>()));

        // The entity adapters: CMS documents and media here, and those packages add with builder.EntityAdapters().
        builder.EntityAdapters()
            .Add<CmsDocumentAdapter>()
            .Add<CmsMediaAdapter>();
        services.AddSingleton(sp =>
        {
            var adapters = new EntityAdapterRegistry();
            foreach (var adapter in sp.GetRequiredService<EntityAdapterCollection>())
            {
                adapters.Add(adapter);
            }

            return adapters;
        });

        // The abilities packages add with builder.Abilities(), beside the framework's own.
        builder.Abilities();

        // The change set service needs the abilities, to replay them, and the change set abilities need the service.
        // So the three are made together.
        services.AddSingleton(sp =>
        {
            var abilities = new AbilityRegistry();
            var adapters = sp.GetRequiredService<EntityAdapterRegistry>();
            var itemTypes = sp.GetRequiredService<ItemTypeRegistry>();
            var changeSets = new ChangeSetService(
                sp.GetRequiredService<IChangeSetStore>(),
                abilities,
                adapters,
                itemTypes,
                sp.GetRequiredService<IChangeSetAccessor>(),
                sp.GetRequiredService<IEventAggregator>(),
                sp.GetRequiredService<IBackgroundTaskQueue>());
            abilities.AddFrameworkAbilities(adapters, itemTypes, changeSets);

            // A package's ability may need the change set service, so it is made once the service is there.
            abilities.AddLater(() => sp.GetRequiredService<AbilityCollection>());
            return new AbilityInvoker(abilities, changeSets);
        });
        services.AddSingleton(sp => sp.GetRequiredService<AbilityInvoker>().ChangeSets);
        services.AddSingleton(sp => sp.GetRequiredService<AbilityInvoker>().Abilities);

        // Which tools each consumer offers: the framework's filters and policies, the filters and consumers packages
        // add (with builder.AbilityFilters() and builder.AbilityConsumers()), and the site's overrides from configuration.
        builder.AbilityFilters();
        builder.AbilityConsumers();
        var overrides = builder.Config.GetSection("Umbraco:Abilities:Consumers").Get<AbilityOverrides>() ?? new AbilityOverrides();
        services.AddSingleton<AbilityPolicyRegistry>();
        services.AddSingleton(sp =>
        {
            var filters = new AbilityFilterRegistry().AddFrameworkFilters(sp.GetRequiredService<AbilityPolicyRegistry>());
            foreach (var filter in sp.GetRequiredService<AbilityFilterCollection>())
            {
                filters.Add(filter);
            }

            return filters;
        });
        services.AddSingleton(sp =>
        {
            var consumers = new AbilityConsumerRegistry();
            foreach (var consumer in sp.GetRequiredService<AbilityConsumerCollection>())
            {
                consumers.Add(consumer);
            }

            return consumers;
        });
        services.AddSingleton(sp =>
        {
            var provider = new AbilityToolProvider(
                sp.GetRequiredService<AbilityRegistry>(),
                sp.GetRequiredService<AbilityFilterRegistry>(),
                sp.GetRequiredService<AbilityPolicyRegistry>(),
                sp.GetRequiredService<AbilityConsumerRegistry>(),
                overrides,
                sp);

            // A filter, policy or consumer named wrongly stops the site the first time tools are asked for.
            provider.Validate();
            return provider;
        });
        services.AddSingleton<AbilityToolRunner>();

        return builder;
    }
}
