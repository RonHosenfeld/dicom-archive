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
    ILogger<RouterService> logger)
{
    private static readonly ActivitySource ActivitySource = new("DicomArchive.Router");
    private static readonly SemaphoreSlim ProcessLock = new(1, 1);

    private const int BatchSize = 100;
    private const int MaxParallelDestinations = 4;

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
            logger.LogInformation("No on-receive rules matched instance {Id} (modality={Modality}, sendingAe={SendingAe}, receivingAe={ReceivingAe})",
                instanceId, modality, sendingAe, receivingAe);
            return 0;
        }

        logger.LogInformation("Matched {Count} destination(s) for instance {Id}: {Destinations}",
            matchingDestinations.Count, instanceId,
            string.Join(", ", matchingDestinations.Select(d => $"{d.Destination.Name}[mode={d.Destination.RoutingMode}]")));

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
                    logger.LogInformation("Remote route already published: study={StudyUid} dest={DestId} — skipping duplicate",
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
                    Status         = "published",
                    InstanceCount  = instanceCount,
                    PublishedAt    = DateTime.UtcNow,
                };
                db.RemoteRoutingLog.Add(entry);
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

    /// <summary>
    /// Processes pending routing queue entries. Returns the number of entries processed.
    /// Uses a semaphore to ensure only one instance runs at a time — additional callers
    /// skip since the running instance will pick up their work.
    /// </summary>
    public async Task<int> ProcessQueueAsync()
    {
        if (!await ProcessLock.WaitAsync(0)) return 0; // skip if already running
        try
        {
            return await ProcessQueueCoreAsync();
        }
        finally
        {
            ProcessLock.Release();
        }
    }

    private async Task<int> ProcessQueueCoreAsync()
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
            .Take(BatchSize)
            .ToListAsync();

        if (pending.Count == 0) return 0;

        logger.LogInformation("Processing {Count} pending route(s)", pending.Count);

        // Detach all entities so each batch can use its own DbContext
        foreach (var entry in pending)
            db.Entry(entry).State = EntityState.Detached;

        // Group by destination so we can reuse DICOM associations
        var groups = pending
            .Where(e => e.Instance != null && e.Destination != null)
            .GroupBy(e => e.DestinationId);

        await Parallel.ForEachAsync(groups,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDestinations },
            async (group, ct) =>
            {
                await SendBatchAsync(group.ToList());
            });

        return pending.Count;
    }

    /// <summary>
    /// Sends all entries in a batch to a single destination using one DICOM association.
    /// Each entry gets its own DB status update so partial failures are tracked.
    /// </summary>
    private async Task SendBatchAsync(List<RoutingLogEntry> entries)
    {
        if (entries.Count == 0) return;

        var dest = entries[0].Destination!;
        using var activity = ActivitySource.StartActivity("SendBatch");
        activity?.SetTag("dest.ae_title", dest.AeTitle);
        activity?.SetTag("dest.host", dest.Host);
        activity?.SetTag("dest.port", dest.Port);
        activity?.SetTag("batch.count", entries.Count);

        logger.LogInformation(
            "Sending batch of {Count} instance(s) → {DestName} [{AeTitle}@{Host}:{Port}]",
            entries.Count, dest.Name, dest.AeTitle, dest.Host, dest.Port);

        // Each entry needs its own DbContext for independent status updates
        await using var db = await dbFactory.CreateDbContextAsync();

        // Prepare all files and DICOM objects
        var prepared = new List<(RoutingLogEntry Entry, DicomFile File, string TempPath)>();
        try
        {
            foreach (var entry in entries)
            {
                try
                {
                    entry.Status = "sending";
                    db.RoutingLog.Attach(entry);
                    db.Entry(entry).Property(e => e.Status).IsModified = true;
                    await db.SaveChangesAsync();

                    var localPath = await storage.FetchToTempAsync(entry.Instance!.BlobKey);
                    var dicomFile = await DicomFile.OpenAsync(localPath);
                    prepared.Add((entry, dicomFile, localPath));
                }
                catch (Exception ex)
                {
                    entry.Attempts++;
                    entry.Status = "failed";
                    entry.LastError = ex.Message;
                    await db.SaveChangesAsync();
                    logger.LogError(ex, "  ✗ Failed to prepare {InstanceUid}", entry.Instance!.InstanceUid);
                }
            }

            if (prepared.Count == 0) return;

            // Send all prepared files on a single DICOM association
            var results = await CStoreBatchAsync(
                prepared.Select(p => p.File).ToList(),
                dest.AeTitle, dest.Host, dest.Port);

            // Update status for each entry
            for (int i = 0; i < prepared.Count; i++)
            {
                var (entry, _, _) = prepared[i];
                var (ok, error) = results[i];

                entry.Attempts++;
                entry.Status = ok ? "success" : "failed";
                entry.LastError = error;
                entry.SentAt = ok ? DateTime.UtcNow : null;
                await db.SaveChangesAsync();

                if (ok)
                    logger.LogInformation("  ✓ Sent {InstanceUid} → {DestName}",
                        entry.Instance!.InstanceUid, dest.Name);
                else
                    logger.LogError("  ✗ Failed {InstanceUid} → {DestName}: {Error}",
                        entry.Instance!.InstanceUid, dest.Name, error);
            }
        }
        finally
        {
            foreach (var (_, _, tempPath) in prepared)
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
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
        var results = await CStoreBatchAsync([file], aeTitle, host, port);
        return results[0];
    }

    /// <summary>
    /// Sends multiple DICOM files on a single association to reduce TCP overhead.
    /// Returns one result per file in the same order as the input list.
    /// </summary>
    private async Task<List<(bool Ok, string? Error)>> CStoreBatchAsync(
        List<DicomFile> files, string aeTitle, string host, int port)
    {
        var results = new (bool Ok, string? Error)[files.Count];
        // Default all to failure in case association fails before any response
        for (int i = 0; i < results.Length; i++)
            results[i] = (false, "No response");

        var client = DicomClientFactory.Create(host, port, false, "ARCHIVE_SCU", aeTitle);
        client.NegotiateAsyncOps();

        for (int i = 0; i < files.Count; i++)
        {
            var index = i; // capture for closure
            var request = new DicomCStoreRequest(files[i]);
            request.OnResponseReceived = (req, response) =>
            {
                results[index] = response.Status == DicomStatus.Success
                    ? (true, null)
                    : (false, $"C-STORE status: {response.Status}");
            };
            await client.AddRequestAsync(request);
        }

        try
        {
            await client.SendAsync();
        }
        catch (Exception ex)
        {
            // Mark any entries that didn't get a response as association failure
            for (int i = 0; i < results.Length; i++)
            {
                if (!results[i].Ok && results[i].Error == "No response")
                    results[i] = (false, $"Association failed: {ex.Message}");
            }
        }

        return results.ToList();
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
