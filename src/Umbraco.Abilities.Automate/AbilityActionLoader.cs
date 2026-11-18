using Microsoft.Extensions.DependencyInjection;

using Umbraco.Abilities.Consumers;
using Umbraco.Abilities.Entities;
using Umbraco.Automate.Core.Actions;

namespace Umbraco.Abilities.Automate;

/// <summary>
/// Makes the site's Automate actions, at the time it's asked: an action per tool the Automate consumer offers, and a
/// Create and Update action per subtype of each entity type that can be created.
/// </summary>
public sealed class AbilityActionLoader(AbilityStep step, AbilityToolProvider tools, EntityAdapterRegistry adapters, AbilityActionOptions options)
{
    /// <summary>The Automate consumer's alias.</summary>
    public const string ConsumerAlias = "automate";

    /// <summary>
    /// Umbraco Automate: a person builds a step from a form, and publishing the automation is their consent to it. Each
    /// step stands alone, with no conversation to keep a change set open in.
    /// </summary>
    public static IAbilityConsumer Consumer { get; } = new AbilityConsumer(
        ConsumerAlias,
        "Umbraco Automate",
        "Automation steps a person builds from forms. Publishing an automation is the person's consent to its steps.",
        AbilityAudiences.Person,
        ConsumerCapabilities.StandingConsent,
        [FrameworkFilters.ForForms]);

    public IEnumerable<IAction> CreateActions()
    {
        var abilities = tools.GetTools(ConsumerAlias)
            .Select(tool => (IAction)new AbilityAction(tool, step, options));

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
        services.AddSingleton(AbilityActionLoader.Consumer);
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
