using System.Data;
using DicomArchive.Server.Data;
using Microsoft.EntityFrameworkCore.Storage;
using DicomArchive.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

/// <summary>
/// 3-step ingest handshake endpoints used by edge agents.
/// Replaces the old /internal/* endpoints. All calls require X-Api-Key auth.
/// </summary>
public static class IngestEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/ingest")
            .RequireAuthorization("AgentPolicy");

        group.MapPost("/prepare", Prepare);
        group.MapPut("/upload/{instanceId:int}", Upload).DisableAntiforgery();
        group.MapPost("/confirm", Confirm);

        // Keep register/heartbeat under /ingest — agents need these too
        group.MapPost("/register",  Register);
        group.MapPost("/heartbeat", Heartbeat);

        // Study/instance download endpoints for remote agents
        group.MapGet("/studies/{studyUid}/instances", ListStudyInstances);
        group.MapGet("/instances/{instanceUid}/download", DownloadInstance);

        // Remote routing: polling + acknowledgment
        group.MapGet("/pending-routes", PendingRoutes);
        group.MapPost("/remote-routing/{id:int}/ack", AckRemoteRouting);

        // Manual routing (used by UI, does not require agent auth)
        app.MapPost("/api/route/instance/{instanceUid}/to/{destId:int}", RouteInstance);
        app.MapPost("/api/route/study/{studyUid}/to/{destId:int}",       RouteStudy);

        // Remote routing log (used by UI)
        app.MapGet("/api/remote-routing-log", RemoteRoutingLog);
    }

    /// <summary>
    /// Executes an INSERT ... ON CONFLICT ... RETURNING id and returns the id.
    /// Uses raw ADO.NET because EF Core's SqlQueryRaw cannot compose over
    /// non-composable SQL (INSERT/UPDATE with RETURNING).
    /// </summary>
    private static async Task<int> UpsertReturningIdAsync(ArchiveDbContext db, string sql, params object?[] parameters)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"p{i}";
            p.Value = parameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    // ── Step 1: Prepare ────────────────────────────────────────────────────────

    static async Task<IResult> Prepare(
        ArchiveDbContext db,
        HttpRequest request,
        ILogger<Program> logger,
        [FromBody] PrepareRequest body)
    {
        if (string.IsNullOrEmpty(body.InstanceUid))
            return Results.BadRequest(new { ok = false, error = "Missing instance_uid" });

        // Use raw SQL upserts — safe under concurrent requests from parallel workers.

        // ── Upsert patient ──
        var patientId = await UpsertReturningIdAsync(db, """
            INSERT INTO patients (patient_id, name, created_at)
            VALUES (@p0, @p1, NOW())
            ON CONFLICT (patient_id) DO UPDATE
                SET name = COALESCE(EXCLUDED.name, patients.name)
            RETURNING id
            """, body.PatientId ?? "UNKNOWN", body.PatientName);

        // ── Upsert exam ──
        var examId = await UpsertReturningIdAsync(db, """
            INSERT INTO exams (patient_id, study_uid, study_date, modality,
                               accession, description, referring_physician, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, NOW())
            ON CONFLICT (study_uid) DO UPDATE
                SET study_date          = COALESCE(EXCLUDED.study_date, exams.study_date),
                    accession           = COALESCE(EXCLUDED.accession, exams.accession),
                    description         = COALESCE(EXCLUDED.description, exams.description),
                    referring_physician = COALESCE(EXCLUDED.referring_physician, exams.referring_physician)
            RETURNING id
            """, patientId, body.StudyUid ?? "",
                 (object?)ParseDate(body.StudyDate) ?? DBNull.Value,
                 body.Modality,
                 (object?)body.AccessionNumber ?? DBNull.Value,
                 (object?)body.StudyDescription ?? DBNull.Value,
                 (object?)body.ReferringPhysician ?? DBNull.Value);

        // ── Upsert series ──
        var seriesId = await UpsertReturningIdAsync(db, """
            INSERT INTO series (exam_id, series_uid, body_part,
                                series_number, description, laterality, view_position, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, NOW())
            ON CONFLICT (series_uid) DO UPDATE
                SET body_part      = COALESCE(EXCLUDED.body_part, series.body_part),
                    series_number  = COALESCE(EXCLUDED.series_number, series.series_number),
                    description    = COALESCE(EXCLUDED.description, series.description),
                    laterality     = COALESCE(EXCLUDED.laterality, series.laterality),
                    view_position  = COALESCE(EXCLUDED.view_position, series.view_position)
            RETURNING id
            """, examId, body.SeriesUid ?? "", body.BodyPart,
                 (object?)body.SeriesNumber ?? DBNull.Value,
                 (object?)body.SeriesDescription ?? DBNull.Value,
                 (object?)body.Laterality ?? DBNull.Value,
                 (object?)body.ViewPosition ?? DBNull.Value);

        // ── Build blob key ──
        var studyDate = body.StudyDate ?? "UNKNOWN";
        var blobKey = $"{studyDate}/{body.StudyUid}/{body.SeriesUid}/{body.InstanceUid}.dcm";

        // ── Upsert instance (status=pending) ──
        var instanceId = await UpsertReturningIdAsync(db, """
            INSERT INTO instances (series_id, instance_uid, blob_key, size_bytes, sha256,
                                   sending_ae, receiving_ae, received_at, status,
                                   instance_number, transfer_syntax, rows, columns)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, NOW(), 'pending', @p7, @p8, @p9, @p10)
            ON CONFLICT (instance_uid) DO UPDATE
                SET status          = 'pending',
                    sha256          = EXCLUDED.sha256,
                    blob_key        = EXCLUDED.blob_key,
                    instance_number = COALESCE(EXCLUDED.instance_number, instances.instance_number),
                    transfer_syntax = COALESCE(EXCLUDED.transfer_syntax, instances.transfer_syntax),
                    rows            = COALESCE(EXCLUDED.rows, instances.rows),
                    columns         = COALESCE(EXCLUDED.columns, instances.columns)
            RETURNING id
            """, seriesId, body.InstanceUid, blobKey,
                 (object?)body.FileSizeBytes ?? DBNull.Value,
                 body.Sha256,
                 body.SendingAe,
                 body.AeTitle,
                 (object?)body.InstanceNumber ?? DBNull.Value,
                 (object?)body.TransferSyntax ?? DBNull.Value,
                 (object?)body.Rows ?? DBNull.Value,
                 (object?)body.Columns ?? DBNull.Value);

        // ── Build upload URL ──
        // Return a server-proxied upload URL. The agent PUTs the file here; the server
        // writes it to blob storage. This avoids Docker networking issues with SAS URLs
        // and works with any storage backend (local, Azure, S3).
        var scheme = request.Scheme;
        var host = request.Host;
        var uploadUrl = $"{scheme}://{host}/ingest/upload/{instanceId}";
        var expiresAt = DateTime.UtcNow.AddMinutes(30).ToString("o");

        logger.LogInformation("Prepare: instance {Uid} → blob {Key}", body.InstanceUid, blobKey);

        return Results.Ok(new
        {
            ok                    = true,
            instance_id           = instanceId,
            blob_key              = blobKey,
            upload_url            = uploadUrl,
            upload_url_expires_at = expiresAt,
        });
    }

    // ── Step 2: Upload (server-proxied) ────────────────────────────────────────

    static async Task<IResult> Upload(
        ArchiveDbContext db,
        StorageService storage,
        ILogger<Program> logger,
        int instanceId,
        HttpRequest request)
    {
        var instance = await db.Instances.FindAsync(instanceId);
        if (instance is null)
            return Results.NotFound(new { ok = false, error = "Instance not found" });

        if (string.IsNullOrEmpty(instance.BlobKey))
            return Results.BadRequest(new { ok = false, error = "Instance has no blob_key — call /prepare first" });

        await storage.StoreFromStreamAsync(instance.BlobKey, request.Body);

        logger.LogInformation("Upload: instance {Id} blob written to {Key}", instanceId, instance.BlobKey);

        return Results.Ok(new { ok = true });
    }

    // ── Step 3: Confirm ────────────────────────────────────────────────────────

    static async Task<IResult> Confirm(
        ArchiveDbContext db,
        RouterService router,
        StorageService storage,
        ILogger<Program> logger,
        [FromBody] ConfirmRequest body)
    {
        var instance = await db.Instances
            .Include(i => i.Series)
            .ThenInclude(s => s.Exam)
            .FirstOrDefaultAsync(i => i.Id == body.InstanceId);

        if (instance is null)
            return Results.NotFound(new { ok = false, error = "Instance not found" });

        // Verify SHA-256 matches what was declared at prepare time
        if (!string.IsNullOrEmpty(body.Sha256) &&
            !string.IsNullOrEmpty(instance.Sha256) &&
            !string.Equals(instance.Sha256, body.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { ok = false, error = "SHA-256 mismatch" });
        }

        instance.Status = "stored";
        if (!string.IsNullOrEmpty(instance.BlobKey))
            instance.BlobUri = storage.GetBlobUri(instance.BlobKey);
        await db.SaveChangesAsync();

        // ── Trigger routing engine ──
        var queued = await router.EvaluateAndQueueAsync(
            instance.Id,
            instance.Series?.Exam?.Modality ?? "",
            instance.SendingAe             ?? "",
            instance.ReceivingAe           ?? "",
            instance.Series?.BodyPart      ?? "",
            instance.Series?.Exam?.StudyUid         ?? "",
            instance.Series?.Exam?.Description      ?? "",
            instance.Series?.Exam?.ReferringPhysician ?? ""
        );

        if (queued > 0)
            _ = Task.Run(() => router.ProcessQueueAsync());

        logger.LogInformation("Confirm: instance {Id} stored, {Routes} route(s) queued",
            instance.Id, queued);

        return Results.Ok(new { ok = true, routes_queued = queued });
    }

    // ── Register / Heartbeat ───────────────────────────────────────────────────

    static async Task<IResult> Register(
        ArchiveDbContext db,
        [FromBody] AgentRegistration? body,
        ILogger<Program> logger)
    {
        if (body?.AeTitle is null) return Results.BadRequest("Missing body or ae_title");
        var ae = body.AeTitle.ToUpper();

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.AeTitle == ae);
        if (agent is null)
        {
            agent = new Agent { AeTitle = ae, FirstSeen = DateTime.UtcNow };
            db.Agents.Add(agent);
        }

        agent.Host           = body.Host;
        agent.ListenPort     = body.ListenPort     ?? agent.ListenPort;
        agent.StorageBackend = body.StorageBackend ?? agent.StorageBackend;
        agent.Version        = body.Version        ?? agent.Version;
        agent.LastSeen       = DateTime.UtcNow;

        await db.SaveChangesAsync();
        logger.LogInformation("Agent registered: [{AeTitle}] from {Host}", ae, body.Host);

        var config = new Dictionary<string, int>();
        if (agent.ConfigInstanceConcurrency is int ic) config["instance_concurrency"] = ic;

        return Results.Ok(new { ok = true, agent, config });
    }

    static async Task<IResult> Heartbeat(ArchiveDbContext db, [FromBody] AgentHeartbeat? body)
    {
        if (body?.AeTitle is null) return Results.BadRequest("Missing body or ae_title");
        var ae = body.AeTitle.ToUpper();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.AeTitle == ae);

        if (agent is null)
        {
            agent = new Agent { AeTitle = ae, FirstSeen = DateTime.UtcNow };
            db.Agents.Add(agent);
        }

        agent.LastSeen          = DateTime.UtcNow;
        agent.InstancesReceived += body.InstancesDelta;
        await db.SaveChangesAsync();

        var config = new Dictionary<string, int>();
        if (agent.ConfigInstanceConcurrency is int ic) config["instance_concurrency"] = ic;

        return Results.Ok(new { ok = true, config });
    }

    // ── Pending routes (polling endpoint for remote agents) ─────────────────────

    static async Task<IResult> PendingRoutes(
        ArchiveDbContext db,
        ILogger<Program> logger,
        [FromQuery(Name = "agent_ae")] string? agentAe)
    {
        if (string.IsNullOrEmpty(agentAe))
            return Results.BadRequest(new { error = "Missing agent_ae query parameter" });

        var ae = agentAe.ToUpper();

        // Atomically claim up to 5 published entries for this agent using raw ADO.NET
        // (EF Core's SqlQueryRaw cannot handle UPDATE ... RETURNING)
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var claimedIds = new List<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE remote_routing_log
                SET status = 'claimed', claimed_at = NOW()
                WHERE id IN (
                    SELECT id FROM remote_routing_log
                    WHERE remote_agent_ae = @p0 AND status = 'published'
                    ORDER BY published_at
                    LIMIT 5
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id
                """;
            var p = cmd.CreateParameter();
            p.ParameterName = "p0";
            p.Value = ae;
            cmd.Parameters.Add(p);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                claimedIds.Add(reader.GetInt32(0));
        }

        if (claimedIds.Count == 0)
            return Results.Ok(Array.Empty<object>());

        // Fetch destination details for the claimed entries
        var entries = await db.RemoteRoutingLog
            .Include(r => r.Destination)
            .Where(r => claimedIds.Contains(r.Id))
            .ToListAsync();

        var result = entries.Select(e => new
        {
            id = e.Id,
            study_uid = e.StudyUid,
            destination_id = e.DestinationId,
            destination_ae_title = e.Destination?.AeTitle,
            destination_host = e.Destination?.Host,
            destination_port = e.Destination?.Port ?? 104,
            instance_count = e.InstanceCount,
        }).ToList();

        logger.LogInformation("Agent {Ae} claimed {Count} pending route(s)", ae, result.Count);
        return Results.Ok(result);
    }

    // ── Study download for remote agents ────────────────────────────────────────

    static async Task<IResult> ListStudyInstances(ArchiveDbContext db, string studyUid)
    {
        var instances = await db.Instances
            .Where(i => i.Series.Exam.StudyUid == studyUid)
            .Select(i => new
            {
                instance_uid = i.InstanceUid,
                blob_key = i.BlobKey,
                size_bytes = i.SizeBytes,
                sha256 = i.Sha256,
                series_uid = i.Series.SeriesUid,
                modality = i.Series.Exam.Modality,
            })
            .ToListAsync();

        return Results.Ok(instances);
    }

    static async Task<IResult> DownloadInstance(
        ArchiveDbContext db,
        DicomArchive.Server.Services.StorageService storage,
        string instanceUid,
        [FromQuery(Name = "destination_id")] int? destinationId = null)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);
        if (instance is null) return Results.NotFound(new { error = "Instance not found" });

        var localPath = await storage.FetchToTempAsync(instance.BlobKey);

        // Apply coercion if a destination with coercion settings is specified
        if (destinationId.HasValue)
        {
            var dest = await db.AeDestinations.FindAsync(destinationId.Value);
            if (dest is not null && !string.IsNullOrEmpty(dest.CoercionAction) && !string.IsNullOrEmpty(dest.CoercionPrefix))
            {
                var dicomFile = await FellowOakDicom.DicomFile.OpenAsync(localPath);
                DicomArchive.Server.Services.CoercionService.Apply(dicomFile.Dataset, dest.CoercionAction, dest.CoercionPrefix);
                var coercedPath = localPath + ".coerced.dcm";
                await dicomFile.SaveAsync(coercedPath);
                try { File.Delete(localPath); } catch { }
                localPath = coercedPath;
            }
        }

        return Results.File(localPath, "application/dicom", $"{instanceUid}.dcm");
    }

    // ── Remote routing acknowledgment ────────────────────────────────────────────

    static async Task<IResult> AckRemoteRouting(
        ArchiveDbContext db, int id, ILogger<Program> logger)
    {
        var entry = await db.RemoteRoutingLog.FindAsync(id);
        if (entry is null) return Results.NotFound(new { error = "Remote routing entry not found" });

        entry.Status = "delivered";
        entry.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Remote route {Id} acknowledged as delivered (study={StudyUid})", id, entry.StudyUid);
        return Results.Ok(new { ok = true });
    }

    // ── Remote routing log ───────────────────────────────────────────────────────

    static async Task<IResult> RemoteRoutingLog(ArchiveDbContext db, int limit = 50)
    {
        var log = await db.RemoteRoutingLog
            .Include(r => r.Destination)
            .Include(r => r.Rule)
            .OrderByDescending(r => r.PublishedAt)
            .Take(Math.Min(limit, 200))
            .Select(r => new
            {
                r.Id, r.StudyUid, r.RemoteAgentAe, r.Status,
                r.InstanceCount, r.InstancesDelivered,
                r.LastError, r.PublishedAt, r.CompletedAt,
                DestinationName = r.Destination != null ? r.Destination.Name : null,
                RuleName = r.Rule != null ? r.Rule.Name : null,
            })
            .ToListAsync();
        return Results.Ok(log);
    }

    // ── Manual routing (unchanged, no auth required) ───────────────────────────

    static async Task<IResult> RouteInstance(
        RouterService router, string instanceUid, int destId)
    {
        _ = Task.Run(() => router.RouteInstanceAsync(instanceUid, destId));
        return Results.Ok(new { ok = true, message = $"Routing {instanceUid} queued" });
    }

    static async Task<IResult> RouteStudy(
        RouterService router, string studyUid, int destId)
    {
        _ = Task.Run(() => router.RouteStudyAsync(studyUid, destId));
        return Results.Ok(new { ok = true, message = $"Routing study {studyUid} queued" });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static DateOnly? ParseDate(string? val)
    {
        if (string.IsNullOrEmpty(val) || val.Length != 8) return null;
        if (int.TryParse(val[..4], out var y) &&
            int.TryParse(val[4..6], out var m) &&
            int.TryParse(val[6..8], out var d))
        {
            try { return new DateOnly(y, m, d); }
            catch { return null; }
        }
        return null;
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record PrepareRequest
{
    public string? AeTitle { get; init; }
    public string? SendingAe { get; init; }
    public string? PatientId { get; init; }
    public string? PatientName { get; init; }
    public string? StudyUid { get; init; }
    public string? StudyDate { get; init; }
    public string? SeriesUid { get; init; }
    public string? InstanceUid { get; init; }
    public string? Modality { get; init; }
    public string? BodyPart { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? Sha256 { get; init; }
    // Exam-level
    public string? StudyDescription { get; init; }
    public string? AccessionNumber { get; init; }
    public string? ReferringPhysician { get; init; }
    // Series-level
    public int? SeriesNumber { get; init; }
    public string? SeriesDescription { get; init; }
    public string? Laterality { get; init; }
    public string? ViewPosition { get; init; }
    // Instance-level
    public int? InstanceNumber { get; init; }
    public string? TransferSyntax { get; init; }
    public int? Rows { get; init; }
    public int? Columns { get; init; }
}

public record ConfirmRequest
{
    public int InstanceId { get; init; }
    public string? Sha256 { get; init; }
}

