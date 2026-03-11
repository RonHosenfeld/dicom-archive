namespace DicomArchive.Server.Services;

/// <summary>
/// Background service that processes the routing queue every 30 seconds.
/// This is the safety net — routes that weren't triggered immediately by
/// the agent's notification will be picked up here.
/// </summary>
public class QueueProcessorService(
    RouterService router,
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
