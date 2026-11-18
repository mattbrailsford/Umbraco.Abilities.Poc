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
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.DraftSets;

namespace Umbraco.Abilities.Cms;

/// <summary>Registers the abilities framework on a real Umbraco site, with CMS documents and media as its first entity types.</summary>
public static class AbilitiesBuilderExtensions
{
    /// <summary>
    /// Registers the framework as singletons: the ability, adapter and item type registries, the change set service
    /// (change sets kept in memory), the invoker, the tool runner, the filter, policy and consumer registries with the
    /// tool provider, and the document and media adapters. Saves run as the signed-in backoffice user. A product adds
    /// its own adapters and abilities to <see cref="EntityAdapterRegistry"/> and <see cref="AbilityRegistry"/>, and its
    /// own filters, policies and consumers to theirs.
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
        services.AddSingleton<CmsScopeUnitOfWorkProvider>();
        services.AddSingleton<CmsContentSchemas>();
        services.AddSingleton(sp => new CmsDocumentAdapter(
            sp.GetRequiredService<CmsContentSchemas>(),
            sp.GetRequiredService<IContentTypeService>(),
            sp.GetRequiredService<IContentEditingService>(),
            sp.GetRequiredService<IEntityService>(),
            sp.GetRequiredService<DraftSetService>(),
            sp.GetRequiredService<IUserService>(),
            sp.GetRequiredService<IContentPermissionService>(),
            sp.GetRequiredService<CmsScopeUnitOfWorkProvider>(),
            ActingUser(sp)));
        services.AddSingleton(sp => new CmsMediaAdapter(
            sp.GetRequiredService<CmsContentSchemas>(),
            sp.GetRequiredService<IMediaTypeService>(),
            sp.GetRequiredService<IMediaEditingService>(),
            sp.GetRequiredService<IEntityService>(),
            sp.GetRequiredService<IUserService>(),
            sp.GetRequiredService<IMediaPermissionService>(),
            sp.GetRequiredService<CmsScopeUnitOfWorkProvider>(),
            ActingUser(sp)));
        services.AddSingleton(sp => new EntityAdapterRegistry()
            .Add(sp.GetRequiredService<CmsDocumentAdapter>())
            .Add(sp.GetRequiredService<CmsMediaAdapter>()));

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
            return new AbilityInvoker(abilities, changeSets);
        });
        services.AddSingleton(sp => sp.GetRequiredService<AbilityInvoker>().ChangeSets);
        services.AddSingleton(sp => sp.GetRequiredService<AbilityInvoker>().Abilities);

        // Which tools each consumer offers: the framework's filters and policies, the filters and consumers packages
        // register (as IAbilityFilter and IAbilityConsumer services), and the site's overrides from configuration.
        var overrides = builder.Config.GetSection("Umbraco:Abilities:Consumers").Get<AbilityOverrides>() ?? new AbilityOverrides();
        services.AddSingleton<AbilityPolicyRegistry>();
        services.AddSingleton(sp =>
        {
            var filters = new AbilityFilterRegistry().AddFrameworkFilters(sp.GetRequiredService<AbilityPolicyRegistry>());
            foreach (var filter in sp.GetServices<IAbilityFilter>())
            {
                filters.Add(filter);
            }

            return filters;
        });
        services.AddSingleton(sp =>
        {
            var consumers = new AbilityConsumerRegistry();
            foreach (var consumer in sp.GetServices<IAbilityConsumer>())
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

    // Umbraco saves as a user. Here that is the backoffice user signed in to the request that applies the set.
    private static Func<Guid> ActingUser(IServiceProvider services)
    {
        var security = services.GetRequiredService<IBackOfficeSecurityAccessor>();
        return () => security.BackOfficeSecurity?.CurrentUser?.Key
            ?? throw new InvalidOperationException("No backoffice user is signed in to save as.");
    }
}
