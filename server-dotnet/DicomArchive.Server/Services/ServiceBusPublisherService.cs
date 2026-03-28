using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace DicomArchive.Server.Services;

/// <summary>
/// Publishes study routing commands to Azure Service Bus for remote agents.
/// Wraps a ServiceBusSender for the "routed-exams" topic.
/// Gracefully no-ops if no Service Bus connection string is configured.
/// </summary>
public class ServiceBusPublisherService : IAsyncDisposable
{
    private readonly ServiceBusClient? _client;
    private readonly ServiceBusSender? _sender;
    private readonly ILogger<ServiceBusPublisherService> _logger;
    private readonly string _serverBaseUrl;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public bool IsConfigured => _client is not null;

    public ServiceBusPublisherService(IConfiguration config, ILogger<ServiceBusPublisherService> logger)
    {
        _logger = logger;

        _serverBaseUrl = config["SERVER_BASE_URL"]
            ?? config["ASPNETCORE_URLS"]?.Split(';').FirstOrDefault()
            ?? "http://localhost:8080";

        var connectionString = config.GetConnectionString("service-bus");
        if (string.IsNullOrEmpty(connectionString))
        {
            logger.LogInformation("Service Bus not configured — remote routing disabled");
            return;
        }

        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender("routed-exams");
        logger.LogInformation("Service Bus publisher initialized for topic 'routed-exams'");
    }

    public async Task<string?> PublishStudyRouteAsync(
        string studyUid,
        string remoteAgentAe,
        string destinationAeTitle,
        string destinationHost,
        int destinationPort,
        int ruleId,
        int destinationId,
        int routingLogId,
        int instanceCount,
        CancellationToken ct = default)
    {
        if (_sender is null)
        {
            _logger.LogWarning("Service Bus not configured — cannot publish remote route for study {StudyUid}", studyUid);
            return null;
        }

        var messageId = Guid.NewGuid().ToString();
        var payload = new
        {
            MessageId = messageId,
            StudyUid = studyUid,
            TargetAgentAe = remoteAgentAe,
            DestinationAeTitle = destinationAeTitle,
            DestinationHost = destinationHost,
            DestinationPort = destinationPort,
            ServerBaseUrl = _serverBaseUrl,
            RuleId = ruleId,
            RoutingLogId = routingLogId,
            InstanceCount = instanceCount,
        };

        var message = new ServiceBusMessage(JsonSerializer.Serialize(payload, JsonOpts))
        {
            MessageId = messageId,
            ContentType = "application/json",
            Subject = $"route-study:{studyUid}",
        };

        // Set application property for subscription SQL filtering
        message.ApplicationProperties["TargetAgentAe"] = remoteAgentAe;

        await _sender.SendMessageAsync(message, ct);

        _logger.LogInformation(
            "Published remote route: study={StudyUid} → agent={Agent} dest={Dest} msgId={MsgId}",
            studyUid, remoteAgentAe, destinationAeTitle, messageId);

        return messageId;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }
}
