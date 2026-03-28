using System.Diagnostics;
using System.Text.RegularExpressions;
using DicomArchive.Server.Data;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Services;

/// <summary>
/// Evaluates routing rules against ingested instances and sends matching
/// images to destination AEs via DICOM C-STORE SCU.
/// OpenTelemetry activities are created for every routing operation so the
/// full pipeline is visible in the Aspire dashboard.
/// </summary>
public class RouterService(
    IDbContextFactory<ArchiveDbContext> dbFactory,
    StorageService storage,
    ServiceBusPublisherService serviceBus,
    ILogger<RouterService> logger)
{
    private static readonly ActivitySource ActivitySource = new("DicomArchive.Router");

    // ── Rule evaluation ───────────────────────────────────────────────────────

    public async Task<int> EvaluateAndQueueAsync(
        int instanceId, string modality, string sendingAe,
        string receivingAe, string bodyPart,
        string studyUid = "", string studyDescription = "", string referringPhysician = "")
    {
        using var activity = ActivitySource.StartActivity("EvaluateRules");
        activity?.SetTag("instance.id",    instanceId);
        activity?.SetTag("instance.modality", modality);
        activity?.SetTag("instance.sending_ae", sendingAe);
        activity?.SetTag("instance.receiving_ae", receivingAe);

        await using var db = await dbFactory.CreateDbContextAsync();

        // Phase 1: SQL-level exact-match filtering
        var candidateRules = await db.RoutingRules
            .Where(r => r.Enabled && r.OnReceive)
            .Where(r => r.MatchModality    == null || r.MatchModality    == modality)
            .Where(r => r.MatchAeTitle     == null || r.MatchAeTitle     == sendingAe)
            .Where(r => r.MatchReceivingAe == null || r.MatchReceivingAe == receivingAe)
            .Where(r => r.MatchBodyPart    == null || r.MatchBodyPart    == bodyPart)
            .OrderBy(r => r.Priority)
            .Include(r => r.RuleDestinations)
                .ThenInclude(rd => rd.Destination)
            .ToListAsync();

        // Phase 3: Apply regex patterns in C# (cannot be evaluated in SQL)
        var matchedRules = candidateRules.Where(r =>
        {
            if (!string.IsNullOrEmpty(r.MatchDescriptionPattern) &&
                !Regex.IsMatch(studyDescription, r.MatchDescriptionPattern, RegexOptions.IgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(r.MatchReferringPattern) &&
                !Regex.IsMatch(referringPhysician, r.MatchReferringPattern, RegexOptions.IgnoreCase))
                return false;
            return true;
        }).ToList();

        var matchingDestinations = matchedRules
            .SelectMany(r => r.RuleDestinations
                .Where(rd => rd.Destination.Enabled)
                .Select(rd => new {
                    RuleId = r.Id,
                    RuleName = r.Name,
                    rd.DestinationId,
                    rd.Destination,
                }))
            .ToList();

        if (matchingDestinations.Count == 0)
        {
            logger.LogDebug("No on-receive rules matched instance {Id}", instanceId);
            return 0;
        }

        int queued = 0;
        foreach (var match in matchingDestinations)
        {
            if (match.Destination.RoutingMode == "remote")
            {
                // Remote routing: deduplicate at study level
                if (string.IsNullOrEmpty(studyUid)) continue;

                var alreadyPublished = await db.RemoteRoutingLog
                    .AnyAsync(r => r.StudyUid == studyUid
                                && r.DestinationId == match.DestinationId
                                && r.Status != "failed");
                if (alreadyPublished)
                {
                    logger.LogDebug("Remote route already published: study={StudyUid} dest={DestId}",
                        studyUid, match.DestinationId);
                    continue;
                }

                // Count instances in this study
                var instanceCount = await db.Instances
                    .CountAsync(i => i.Series.Exam.StudyUid == studyUid);

                var entry = new RemoteRoutingLogEntry
                {
                    StudyUid       = studyUid,
                    RuleId         = match.RuleId,
                    DestinationId  = match.DestinationId,
                    RemoteAgentAe  = match.Destination.RemoteAgentAe,
                    Status         = "publishing",
                    InstanceCount  = instanceCount,
                    PublishedAt    = DateTime.UtcNow,
                };
                db.RemoteRoutingLog.Add(entry);
                await db.SaveChangesAsync();

                // Publish to Service Bus
                var messageId = await serviceBus.PublishStudyRouteAsync(
                    studyUid,
                    match.Destination.RemoteAgentAe ?? match.Destination.AeTitle,
                    match.Destination.AeTitle,
                    match.Destination.Host,
                    match.Destination.Port,
                    match.RuleId,
                    match.DestinationId,
                    entry.Id,
                    instanceCount);

                entry.ServiceBusMessageId = messageId;
                entry.Status = messageId is not null ? "published" : "failed";
                if (messageId is null) entry.LastError = "Service Bus not configured";
                await db.SaveChangesAsync();

                logger.LogInformation(
                    "Remote route: study={StudyUid} → agent={Agent} dest={DestId} (rule: {Rule})",
                    studyUid, match.Destination.RemoteAgentAe, match.DestinationId, match.RuleName);
            }
            else
            {
                // Direct routing: queue for C-STORE SCU (existing behavior)
                db.RoutingLog.Add(new RoutingLogEntry
                {
                    InstanceId    = instanceId,
                    RuleId        = match.RuleId,
                    DestinationId = match.DestinationId,
                    Status        = "queued",
                    QueuedAt      = DateTime.UtcNow,
                });
                logger.LogInformation(
                    "Queued route: instance={Id} → dest={DestId} (rule: {Rule})",
                    instanceId, match.DestinationId, match.RuleName);
            }
            queued++;
        }

        await db.SaveChangesAsync();
        activity?.SetTag("routes.queued", queued);
        return queued;
    }

    // ── Queue processing ──────────────────────────────────────────────────────

    public async Task ProcessQueueAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var pending = await db.RoutingLog
            .Include(rl => rl.Instance)
            .Include(rl => rl.Destination)
            .Where(rl => (rl.Status == "queued" || rl.Status == "failed")
                      && rl.Attempts < 3
                      && rl.Destination != null
                      && rl.Destination.Enabled)
            .OrderBy(rl => rl.QueuedAt)
            .ToListAsync();

        if (pending.Count == 0) return;

        logger.LogInformation("Processing {Count} pending route(s)", pending.Count);

        foreach (var entry in pending)
        {
            if (entry.Instance == null || entry.Destination == null) continue;
            await SendInstanceAsync(db, entry);
        }
    }

    // ── Manual routing ────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> RouteInstanceAsync(
        string instanceUid, int destinationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var instance = await db.Instances
            .FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);
        if (instance is null) return (false, "Instance not found");

        var dest = await db.AeDestinations.FindAsync(destinationId);
        if (dest is null) return (false, "Destination not found");

        var entry = new RoutingLogEntry
        {
            InstanceId    = instance.Id,
            DestinationId = destinationId,
            Status        = "queued",
            QueuedAt      = DateTime.UtcNow,
        };
        db.RoutingLog.Add(entry);
        await db.SaveChangesAsync();

        // Load the full entry with navigation properties
        await db.Entry(entry).Reference(e => e.Instance!).LoadAsync();
        await db.Entry(entry).Reference(e => e.Destination!).LoadAsync();

        return await SendInstanceAsync(db, entry);
    }

    public async Task RouteStudyAsync(string studyUid, int destinationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var instanceUids = await db.Instances
            .Where(i => i.Series.Exam.StudyUid == studyUid)
            .Select(i => i.InstanceUid)
            .ToListAsync();

        foreach (var uid in instanceUids)
            await RouteInstanceAsync(uid, destinationId);
    }

    // ── Core send ─────────────────────────────────────────────────────────────

    private async Task<(bool Ok, string? Error)> SendInstanceAsync(
        ArchiveDbContext db, RoutingLogEntry entry)
    {
        var instance = entry.Instance!;
        var dest     = entry.Destination!;

        using var activity = ActivitySource.StartActivity("SendInstance");
        activity?.SetTag("instance.uid",    instance.InstanceUid);
        activity?.SetTag("dest.ae_title",   dest.AeTitle);
        activity?.SetTag("dest.host",       dest.Host);
        activity?.SetTag("dest.port",       dest.Port);

        logger.LogInformation(
            "Sending {InstanceUid} → {DestName} [{AeTitle}@{Host}:{Port}]",
            instance.InstanceUid, dest.Name, dest.AeTitle, dest.Host, dest.Port);

        entry.Status = "sending";
        await db.SaveChangesAsync();

        try
        {
            var localPath = await storage.FetchToTempAsync(instance.BlobKey);
            try
            {
                var dicomFile = await DicomFile.OpenAsync(localPath);
                var (ok, error) = await CStoreAsync(dicomFile, dest.AeTitle, dest.Host, dest.Port);

                entry.Attempts++;
                entry.Status    = ok ? "success" : "failed";
                entry.LastError = error;
                entry.SentAt    = ok ? DateTime.UtcNow : null;
                await db.SaveChangesAsync();

                if (ok)
                    logger.LogInformation("  ✓ Sent {InstanceUid} → {DestName}",
                        instance.InstanceUid, dest.Name);
                else
                    logger.LogError("  ✗ Failed {InstanceUid} → {DestName}: {Error}",
                        instance.InstanceUid, dest.Name, error);

                activity?.SetStatus(ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error, error);
                return (ok, error);
            }
            finally
            {
                File.Delete(localPath);
            }
        }
        catch (Exception ex)
        {
            entry.Attempts++;
            entry.Status    = "failed";
            entry.LastError = ex.Message;
            await db.SaveChangesAsync();

            logger.LogError(ex, "  ✗ Exception routing {InstanceUid}", instance.InstanceUid);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return (false, ex.Message);
        }
    }

    // ── C-STORE SCU ───────────────────────────────────────────────────────────

    private async Task<(bool Ok, string? Error)> CStoreAsync(
        DicomFile file, string aeTitle, string host, int port)
    {
        string? lastError = null;
        bool success = false;

        var client = DicomClientFactory.Create(host, port, false, "ARCHIVE_SCU", aeTitle);
        client.NegotiateAsyncOps();

        var request = new DicomCStoreRequest(file);
        request.OnResponseReceived = (req, response) =>
        {
            if (response.Status == DicomStatus.Success)
                success = true;
            else
                lastError = $"C-STORE status: {response.Status}";
        };

        await client.AddRequestAsync(request);

        try
        {
            await client.SendAsync();
        }
        catch (Exception ex)
        {
            return (false, $"Association failed: {ex.Message}");
        }

        return success ? (true, null) : (false, lastError ?? "No response");
    }

    // ── C-ECHO (connectivity test) ────────────────────────────────────────────

    public async Task<(bool Ok, string Message)> EchoAsync(
        string aeTitle, string host, int port)
    {
        using var activity = ActivitySource.StartActivity("CEcho");
        activity?.SetTag("dest.ae_title", aeTitle);
        activity?.SetTag("dest.host",     host);
        activity?.SetTag("dest.port",     port);

        try
        {
            var client = DicomClientFactory.Create(host, port, false, "ARCHIVE_SCU", aeTitle);
            var echoRequest = new DicomCEchoRequest();
            bool success = false;
            echoRequest.OnResponseReceived = (req, response) =>
            {
                success = response.Status == DicomStatus.Success;
            };
            await client.AddRequestAsync(echoRequest);
            await client.SendAsync();
            return success
                ? (true,  "C-ECHO success")
                : (false, "C-ECHO returned non-success status");
        }
        catch (Exception ex)
        {
            return (false, $"Association failed — {ex.Message}");
        }
    }
}
