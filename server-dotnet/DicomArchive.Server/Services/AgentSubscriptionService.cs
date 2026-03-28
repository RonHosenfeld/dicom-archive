using Azure.Messaging.ServiceBus.Administration;

namespace DicomArchive.Server.Services;

/// <summary>
/// Manages Service Bus subscriptions for remote agents.
/// When an agent registers with remote_routing_enabled=true, a subscription
/// is created on the "routed-exams" topic filtered by its AE title.
/// </summary>
public class AgentSubscriptionService
{
    private readonly ServiceBusAdministrationClient? _adminClient;
    private readonly ILogger<AgentSubscriptionService> _logger;
    private const string TopicName = "routed-exams";

    public bool IsConfigured => _adminClient is not null;

    public AgentSubscriptionService(IConfiguration config, ILogger<AgentSubscriptionService> logger)
    {
        _logger = logger;

        var connectionString = config.GetConnectionString("service-bus");
        if (string.IsNullOrEmpty(connectionString))
        {
            logger.LogInformation("Service Bus not configured — agent subscription management disabled");
            return;
        }

        _adminClient = new ServiceBusAdministrationClient(connectionString);
        logger.LogInformation("Agent subscription service initialized");
    }

    /// <summary>
    /// Ensures a subscription exists for the given agent AE title with a SQL filter
    /// so it only receives messages targeted at that agent.
    /// </summary>
    public async Task EnsureSubscriptionAsync(string agentAeTitle, CancellationToken ct = default)
    {
        if (_adminClient is null) return;

        var subscriptionName = $"agent-{agentAeTitle.ToLower()}";

        try
        {
            // Ensure topic exists
            if (!await _adminClient.TopicExistsAsync(TopicName, ct))
            {
                await _adminClient.CreateTopicAsync(TopicName, ct);
                _logger.LogInformation("Created Service Bus topic '{Topic}'", TopicName);
            }

            // Ensure subscription exists with SQL filter
            if (!await _adminClient.SubscriptionExistsAsync(TopicName, subscriptionName, ct))
            {
                var options = new CreateSubscriptionOptions(TopicName, subscriptionName)
                {
                    MaxDeliveryCount = 5,
                    LockDuration = TimeSpan.FromMinutes(5),
                };

                var rule = new CreateRuleOptions("AgentFilter", new SqlRuleFilter($"TargetAgentAe = '{agentAeTitle}'"));

                await _adminClient.CreateSubscriptionAsync(options, rule, ct);
                _logger.LogInformation(
                    "Created subscription '{Sub}' on '{Topic}' with filter TargetAgentAe='{AE}'",
                    subscriptionName, TopicName, agentAeTitle);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not ensure subscription for agent {AE} — remote routing may not work for this agent",
                agentAeTitle);
        }
    }
}
