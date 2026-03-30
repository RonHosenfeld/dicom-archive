using DicomArchive.Server.Data;
using DicomArchive.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

public static class RuleEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet   ("/api/rules",          List);
        app.MapPost  ("/api/rules",          Create);
        app.MapGet   ("/api/rules/{id:int}", Get);
        app.MapPut   ("/api/rules/{id:int}", Update);
        app.MapDelete("/api/rules/{id:int}", Delete);
        app.MapGet   ("/api/routing-log",    RoutingLog);
        app.MapPost  ("/api/routing-log/{id:int}/resend", ResendRoute);
        app.MapPost  ("/api/routing-log/resend-failed",   ResendAllFailed);
    }

    static async Task<IResult> List(ArchiveDbContext db)
    {
        var rules = await db.RoutingRules
            .Include(r => r.RuleDestinations).ThenInclude(rd => rd.Destination)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync();

        // Shape the response to match the Python API — include destinations as a list
        var result = rules.Select(r => new
        {
            r.Id, r.Name, r.Priority, r.Enabled,
            r.MatchModality, r.MatchAeTitle, r.MatchReceivingAe, r.MatchBodyPart,
            r.MatchDescriptionPattern, r.MatchReferringPattern,
            r.OnReceive, r.Description, r.CreatedAt, r.UpdatedAt,
            Destinations = r.RuleDestinations.Select(rd => new {
                rd.Destination.Id, rd.Destination.Name, rd.Destination.AeTitle,
                rd.Destination.Host, rd.Destination.Port, rd.Destination.Enabled
            }).ToList()
        });

        return Results.Ok(result);
    }

    static async Task<IResult> Get(ArchiveDbContext db, int id)
    {
        var rule = await db.RoutingRules
            .Include(r => r.RuleDestinations)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return Results.NotFound();
        return Results.Ok(new {
            rule.Id, rule.Name, rule.Priority, rule.Enabled,
            rule.MatchModality, rule.MatchAeTitle, rule.MatchReceivingAe, rule.MatchBodyPart,
            rule.MatchDescriptionPattern, rule.MatchReferringPattern,
            rule.OnReceive, rule.Description,
            DestinationIds = rule.RuleDestinations.Select(rd => rd.DestinationId).ToList()
        });
    }

    static async Task<IResult> Create(ArchiveDbContext db, RuleIn body)
    {
        if (body.DestinationIds.Count == 0)
            return Results.BadRequest("At least one destination is required");

        var rule = new RoutingRule
        {
            Name                    = body.Name,
            Priority                = body.Priority,
            Enabled                 = body.Enabled,
            MatchModality           = Normalise(body.MatchModality),
            MatchAeTitle            = Normalise(body.MatchAeTitle),
            MatchReceivingAe        = Normalise(body.MatchReceivingAe),
            MatchBodyPart           = Normalise(body.MatchBodyPart),
            MatchDescriptionPattern = string.IsNullOrWhiteSpace(body.MatchDescriptionPattern) ? null : body.MatchDescriptionPattern.Trim(),
            MatchReferringPattern   = string.IsNullOrWhiteSpace(body.MatchReferringPattern) ? null : body.MatchReferringPattern.Trim(),
            OnReceive               = body.OnReceive,
            Description             = body.Description,
            CreatedAt               = DateTime.UtcNow,
            UpdatedAt               = DateTime.UtcNow,
        };
        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync();

        foreach (var destId in body.DestinationIds)
            db.RuleDestinations.Add(new RuleDestination { RuleId = rule.Id, DestinationId = destId });
        await db.SaveChangesAsync();

        return Results.Created($"/api/rules/{rule.Id}", new {
            rule.Id, rule.Name, rule.Priority, rule.Enabled,
            rule.MatchModality, rule.MatchAeTitle, rule.MatchReceivingAe, rule.MatchBodyPart,
            rule.MatchDescriptionPattern, rule.MatchReferringPattern,
            rule.OnReceive, rule.Description,
            DestinationIds = body.DestinationIds
        });
    }

    static async Task<IResult> Update(ArchiveDbContext db, int id, RuleIn body)
    {
        var rule = await db.RoutingRules
            .Include(r => r.RuleDestinations)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return Results.NotFound();
        if (body.DestinationIds.Count == 0)
            return Results.BadRequest("At least one destination is required");

        rule.Name                    = body.Name;
        rule.Priority                = body.Priority;
        rule.Enabled                 = body.Enabled;
        rule.MatchModality           = Normalise(body.MatchModality);
        rule.MatchAeTitle            = Normalise(body.MatchAeTitle);
        rule.MatchReceivingAe        = Normalise(body.MatchReceivingAe);
        rule.MatchBodyPart           = Normalise(body.MatchBodyPart);
        rule.MatchDescriptionPattern = string.IsNullOrWhiteSpace(body.MatchDescriptionPattern) ? null : body.MatchDescriptionPattern.Trim();
        rule.MatchReferringPattern   = string.IsNullOrWhiteSpace(body.MatchReferringPattern) ? null : body.MatchReferringPattern.Trim();
        rule.OnReceive               = body.OnReceive;
        rule.Description             = body.Description;
        rule.UpdatedAt               = DateTime.UtcNow;

        // Replace destinations
        db.RuleDestinations.RemoveRange(rule.RuleDestinations);
        foreach (var destId in body.DestinationIds)
            db.RuleDestinations.Add(new RuleDestination { RuleId = rule.Id, DestinationId = destId });

        await db.SaveChangesAsync();
        return Results.Ok(new {
            rule.Id, rule.Name, rule.Priority, rule.Enabled,
            rule.MatchModality, rule.MatchAeTitle, rule.MatchReceivingAe, rule.MatchBodyPart,
            rule.MatchDescriptionPattern, rule.MatchReferringPattern,
            rule.OnReceive, rule.Description,
            DestinationIds = body.DestinationIds
        });
    }

    static async Task<IResult> Delete(ArchiveDbContext db, int id)
    {
        var rule = await db.RoutingRules.FindAsync(id);
        if (rule is null) return Results.NotFound();
        db.RoutingRules.Remove(rule);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    static async Task<IResult> RoutingLog(ArchiveDbContext db,
        string? search = null, string? status = null, string? destination = null,
        string? date_from = null, string? date_to = null,
        int limit = 100, int offset = 0)
    {
        // ── Direct routing entries ──
        var q = db.RoutingLog
            .Include(rl => rl.Instance).ThenInclude(i => i!.Series).ThenInclude(s => s.Exam)
            .Include(rl => rl.Destination)
            .Include(rl => rl.Rule)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            q = q.Where(rl => rl.Status == status);
        if (!string.IsNullOrEmpty(destination))
            q = q.Where(rl => rl.Destination != null && rl.Destination.Name == destination);
        if (DateTime.TryParse(date_from, out var df))
            q = q.Where(rl => rl.QueuedAt >= df);
        if (DateTime.TryParse(date_to, out var dt))
            q = q.Where(rl => rl.QueuedAt <= dt.AddDays(1));
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            q = q.Where(rl =>
                (rl.Instance != null && EF.Functions.ILike(rl.Instance.InstanceUid, pattern)) ||
                (rl.Destination != null && EF.Functions.ILike(rl.Destination.Name, pattern)) ||
                (rl.Rule != null && EF.Functions.ILike(rl.Rule.Name, pattern)) ||
                (rl.LastError != null && EF.Functions.ILike(rl.LastError, pattern))
            );
        }

        var directLog = await q
            .OrderByDescending(rl => rl.QueuedAt)
            .Take(Math.Min(limit, 500))
            .Select(rl => new {
                rl.Id, rl.Status, rl.Attempts, rl.LastError,
                rl.QueuedAt, rl.SentAt,
                InstanceUid     = rl.Instance != null ? rl.Instance.InstanceUid : null,
                StudyUid        = rl.Instance != null && rl.Instance.Series != null && rl.Instance.Series.Exam != null
                                  ? rl.Instance.Series.Exam.StudyUid : null,
                Accession       = rl.Instance != null && rl.Instance.Series != null && rl.Instance.Series.Exam != null
                                  ? rl.Instance.Series.Exam.Accession : null,
                DestinationName = rl.Destination != null ? rl.Destination.Name : null,
                DestinationId   = rl.DestinationId,
                RuleName        = rl.Rule != null ? rl.Rule.Name : "Manual",
                Mode            = "direct",
                RemoteAgentAe   = (string?)null,
            })
            .ToListAsync();

        // ── Remote routing entries ──
        var rq = db.RemoteRoutingLog
            .Include(r => r.Destination)
            .Include(r => r.Rule)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            rq = rq.Where(r => r.Status == status);
        if (!string.IsNullOrEmpty(destination))
            rq = rq.Where(r => r.Destination != null && r.Destination.Name == destination);
        if (DateTime.TryParse(date_from, out var rdf))
            rq = rq.Where(r => r.PublishedAt >= rdf);
        if (DateTime.TryParse(date_to, out var rdt))
            rq = rq.Where(r => r.PublishedAt <= rdt.AddDays(1));
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            rq = rq.Where(r =>
                EF.Functions.ILike(r.StudyUid, pattern) ||
                (r.Destination != null && EF.Functions.ILike(r.Destination.Name, pattern)) ||
                (r.Rule != null && EF.Functions.ILike(r.Rule.Name, pattern)) ||
                (r.RemoteAgentAe != null && EF.Functions.ILike(r.RemoteAgentAe, pattern)) ||
                (r.LastError != null && EF.Functions.ILike(r.LastError, pattern))
            );
        }

        var remoteLog = await rq
            .OrderByDescending(r => r.PublishedAt)
            .Take(Math.Min(limit, 500))
            .Select(r => new {
                r.Id, r.Status, Attempts = 0, r.LastError,
                QueuedAt = r.PublishedAt, SentAt = r.CompletedAt,
                InstanceUid     = (string?)null,
                StudyUid        = (string?)r.StudyUid,
                Accession       = (string?)null,
                DestinationName = r.Destination != null ? r.Destination.Name : null,
                DestinationId   = r.DestinationId,
                RuleName        = r.Rule != null ? r.Rule.Name : "Manual",
                Mode            = "remote",
                RemoteAgentAe   = r.RemoteAgentAe,
            })
            .ToListAsync();

        // Merge both lists, sorted by time descending
        var merged = directLog.Concat(remoteLog)
            .OrderByDescending(e => e.QueuedAt)
            .Skip(offset)
            .Take(Math.Min(limit, 500))
            .ToList();

        return Results.Ok(merged);
    }

    static async Task<IResult> ResendRoute(ArchiveDbContext db, RouterService router, int id)
    {
        var entry = await db.RoutingLog.FindAsync(id);
        if (entry is null) return Results.NotFound();
        entry.Status = "queued";
        entry.Attempts = 0;
        entry.LastError = null;
        await db.SaveChangesAsync();
        _ = Task.Run(() => router.ProcessQueueAsync());
        return Results.Ok(new { ok = true });
    }

    static async Task<IResult> ResendAllFailed(ArchiveDbContext db, RouterService router)
    {
        var count = await db.RoutingLog
            .Where(rl => rl.Status == "failed")
            .ExecuteUpdateAsync(s => s
                .SetProperty(rl => rl.Status, "queued")
                .SetProperty(rl => rl.Attempts, 0)
                .SetProperty(rl => rl.LastError, (string?)null));
        if (count > 0)
            _ = Task.Run(() => router.ProcessQueueAsync());
        return Results.Ok(new { ok = true, count });
    }

    private static string? Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpper();
}
