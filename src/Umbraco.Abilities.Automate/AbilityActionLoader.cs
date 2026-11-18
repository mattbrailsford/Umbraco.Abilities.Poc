using Microsoft.Extensions.DependencyInjection;

using Umbraco.Abilities.Entities;
using Umbraco.Automate.Core.Actions;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// Makes the site's Automate actions from its abilities and entity types, at the time it's asked: an action per ability
/// the options include, and a Create and Update action per subtype of each entity type that can be created.
/// </summary>
public sealed class AbilityActionLoader(AbilityStep step, EntityAdapterRegistry adapters, AbilityActionOptions options)
{
    public IEnumerable<IAction> CreateActions()
    {
        var abilities = step.Invoker.Abilities.All
            .Where(options.Include)
            .Select(ability => (IAction)new AbilityAction(ability, step, options));

        return options.EntityActions ? abilities.Concat(EntityActions()) : abilities;
    }

    private IEnumerable<IAction> EntityActions()
    {
        foreach (var adapter in adapters.All.Where(a => a.CanCreate))
        {
            // Automate asks for its actions synchronously, once.
            var subtypes = adapter.GetSubtypesAsync().GetAwaiter().GetResult();
            foreach (var subtype in subtypes.Count == 0 ? [null] : subtypes.Cast<EntitySubtype?>())
            {
                yield return new EntityAction(adapter, subtype, EntityActionKind.Create, step, options);
                yield return new EntityAction(adapter, subtype, EntityActionKind.Update, step, options);
            }
        }
    }
}

public static class AbilityActionServiceCollectionExtensions
{
    /// <summary>
    /// Adds every ability to Automate's actions. Call it after Umbraco's builder has built (in Program.cs, after
    /// <c>.Build()</c>), because only then has Automate registered its action collection.
    /// <para>
    /// FUDGE (registration): Automate's action collection is built from types, one instance per type, found by an
    /// attribute scan. Abilities are instances made at run time, so this replaces the collection with one that holds
    /// Automate's actions and the abilities'. Automate would instead let a package add actions as instances.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAbilityActions(this IServiceCollection services, Action<AbilityActionOptions>? configure = null)
    {
        var automate = services.LastOrDefault(d => d.ServiceType == typeof(ActionCollection))
            ?? throw new InvalidOperationException("Automate's actions aren't registered yet. Call AddAbilityActions after Umbraco's builder has built.");
        var create = automate.ImplementationFactory
            ?? throw new InvalidOperationException("Automate's action collection isn't registered by a factory, so it can't be extended.");

        var options = new AbilityActionOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<AbilityStep>();
        services.AddSingleton<AbilityActionLoader>();
        services.AddSingleton(sp =>
        {
            var own = (ActionCollection)create(sp);
            var loader = sp.GetRequiredService<AbilityActionLoader>();
            return new ActionCollection(() => own.Concat(loader.CreateActions()));
        });

        return services;
    }
}
