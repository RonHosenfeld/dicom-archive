namespace DicomArchive.Server.Data;

// IngestEndpoints
record AgentRegistration(string AeTitle, string Host, int? ListenPort, string? StorageBackend, string? Version, bool? RemoteRoutingEnabled = false);
record AgentHeartbeat(string AeTitle, long InstancesDelta);

// AgentEndpoints
record AgentUpdate(string? Description, bool? Enabled);

// DestinationEndpoints
record DestinationIn(string Name, string AeTitle, string Host, int Port, string? Description, bool Enabled, string RoutingMode = "direct", string? RemoteAgentAe = null, string? CoercionAction = null, string? CoercionPrefix = null);

// RuleEndpoints
record RuleIn(string Name, int Priority, bool Enabled, string? MatchModality, string? MatchAeTitle, string? MatchReceivingAe, string? MatchBodyPart, string? MatchDescriptionPattern, string? MatchReferringPattern, bool OnReceive, string? Description, List<int> DestinationIds);

// StudyEndpoints
record StudySummary(int Id, string StudyUid, DateOnly? StudyDate, string? Accession, string? Description, string? Modality, string PatientId, string? PatientName, DateOnly? BirthDate, int SeriesCount, int InstanceCount);
record StatsResult(long TotalPatients, long TotalStudies, long TotalSeries, long TotalInstances, long TotalBytes,
    long RoutesOk, long RoutesFailed, long RoutesQueued,
    long RemotePublished, long RemoteClaimed, long RemoteDelivered);

// MetricsEndpoints
record MetricsSummary(long ExamsToday, long Exams7d, long Exams30d,
    long InstancesToday, long Instances7d, long Instances30d,
    long BytesToday, long Bytes7d, long Bytes30d,
    long RoutesOk30d, long RoutesFailed30d);
record IngestBucket(DateTime Period, long Exams, long Series, long Instances, long Bytes);
record StorageBucket(DateTime Period, long InstancesAdded, long BytesAdded, long CumulativeBytes);
record StorageMetrics(long BaseBytes, List<StorageBucket> Buckets);
record RoutingBucket(DateTime Period, long Success, long Failed, long Queued, double? AvgLatencySec);
