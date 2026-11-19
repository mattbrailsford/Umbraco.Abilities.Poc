using Microsoft.Extensions.DependencyInjection;

using Umbraco.Automate.Core.Actions;

namespace Umbraco.Abilities.Automate;

/// <summary>Adds every ability the Automate consumer offers to Automate's actions.</summary>
public static class AbilityActionServiceCollectionExtensions
{
    /// <summary>
    /// Adds every ability to Automate's actions. Call it after Umbraco's builder has built (in Program.cs, after
    /// <c>.Build()</c>), because only then has Automate registered its action collection. The Automate consumer is
    /// added before that, with <see cref="AutomateAbilitiesBuilderExtensions.AddAutomateAbilities"/>.
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
