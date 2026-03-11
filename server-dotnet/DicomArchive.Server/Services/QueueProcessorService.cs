namespace DicomArchive.Server.Services;

/// <summary>
/// Background service that processes the routing queue every 30 seconds.
/// Uses IServiceScopeFactory to create a fresh scope per run — BackgroundService
/// is Singleton, so it cannot directly inject Scoped services like RouterService.
/// </summary>
public class QueueProcessorService(
    IServiceScopeFactory scopeFactory,
    ILogger<QueueProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue processor started (interval: 30s)");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var router = scope.ServiceProvider.GetRequiredService<RouterService>();
                await router.ProcessQueueAsync();
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
}
