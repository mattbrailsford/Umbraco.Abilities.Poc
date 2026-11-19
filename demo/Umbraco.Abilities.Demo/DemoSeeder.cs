using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Abilities.Demo;

/// <summary>
/// Seeds the demo once Umbraco is fully up: the MCP API user (<see cref="DemoSetup"/>), then the shop
/// (<see cref="DemoShop"/>). Each part is made only if missing, so it is safe on every start.
/// <para>
/// It runs in the background, not in a start-up notification. On SQLite, Umbraco's own start-up work (such as
/// rebuilding the content cache) holds the database's write lock while those notifications run, so a handler that
/// writes waits for a lock that is never released, and the site never finishes starting. On a fresh install Umbraco
/// 18.3 also runs package migrations (Clean, Commerce) in the background after the site is up; this waits for them.
/// </para>
/// </summary>
public sealed class DemoSeeder(
    IHostApplicationLifetime lifetime,
    IRuntimeState runtime,
    IServiceScopeFactory scopes,
    ILogger<DemoSeeder> logger) : BackgroundService
{
    private const int Attempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Demo: waiting for Umbraco to start.");
        if (!await WhenStartedAsync(stoppingToken) || !await WhenRunningAsync(stoppingToken))
        {
            return;
        }

        logger.LogInformation("Demo: Umbraco is running. Seeding.");

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                // A scope per attempt, as a request has: some of Umbraco's services are scoped.
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DemoSetup>().SeedAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<DemoShop>().SeedAsync(stoppingToken);
                logger.LogInformation("Demo: seeded.");
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested && attempt < Attempts)
            {
                logger.LogWarning(ex, "Demo: seeding failed (attempt {Attempt} of {Attempts}). Trying again shortly.", attempt, Attempts);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> WhenStartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource();
        using var _ = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await Task.WhenAny(started.Task, Task.Delay(Timeout.Infinite, stoppingToken));
        return started.Task.IsCompleted;
    }

    // A fresh install is at Upgrading until its package migrations finish in the background.
    private async Task<bool> WhenRunningAsync(CancellationToken stoppingToken)
    {
        var waited = 0;
        while (runtime.Level != RuntimeLevel.Run)
        {
            if (++waited % 15 == 0)
            {
                logger.LogInformation("Demo: still waiting, Umbraco is at {Level} ({Reason}).", runtime.Level, runtime.Reason);
            }

            if (runtime.Level == RuntimeLevel.BootFailed)
            {
                logger.LogWarning("Demo: Umbraco failed to start, so nothing is seeded.");
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        return true;
    }
}
