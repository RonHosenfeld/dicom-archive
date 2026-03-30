using DicomArchive.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Services;

/// <summary>
/// Background service that processes the routing queue every 30 seconds.
/// Uses IServiceScopeFactory to create a fresh scope per run — BackgroundService
/// is Singleton, so it cannot directly inject Scoped services like RouterService.
/// When a full batch is processed, immediately loops again to drain the queue quickly.
/// </summary>
public class QueueProcessorService(
    IServiceScopeFactory scopeFactory,
    ILogger<QueueProcessorService> logger) : BackgroundService
{
    private const int IdleIntervalSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue processor started (interval: {Interval}s)", IdleIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(IdleIntervalSeconds), stoppingToken);

            try
            {
                // Reset stale claims from crashed agents
                await ResetStaleClaims(stoppingToken);

                int processed;
                do
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var router = scope.ServiceProvider.GetRequiredService<RouterService>();
                    processed = await router.ProcessQueueAsync();

                    if (processed > 0)
                        logger.LogInformation("Queue processor: processed {Count} entries", processed);

                } while (processed >= 100 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queue processor error");
            }
        }

        logger.LogInformation("Queue processor stopped");
    }

    private async Task ResetStaleClaims(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
            var reset = await db.Database.ExecuteSqlRawAsync("""
                UPDATE remote_routing_log SET status = 'published', claimed_at = NULL
                WHERE status = 'claimed' AND claimed_at < NOW() - INTERVAL '5 minutes'
                """, ct);
            if (reset > 0)
                logger.LogInformation("Reset {Count} stale remote routing claim(s)", reset);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resetting stale claims");
        }
    }
}
