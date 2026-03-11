using DicomArchive.Server.Data;
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
            Name             = body.Name,
            Priority         = body.Priority,
            Enabled          = body.Enabled,
            MatchModality    = Normalise(body.MatchModality),
            MatchAeTitle     = Normalise(body.MatchAeTitle),
            MatchReceivingAe = Normalise(body.MatchReceivingAe),
            MatchBodyPart    = Normalise(body.MatchBodyPart),
            OnReceive        = body.OnReceive,
            Description      = body.Description,
            CreatedAt        = DateTime.UtcNow,
            UpdatedAt        = DateTime.UtcNow,
        };
        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync();

        foreach (var destId in body.DestinationIds)
            db.RuleDestinations.Add(new RuleDestination { RuleId = rule.Id, DestinationId = destId });
        await db.SaveChangesAsync();

        return Results.Created($"/api/rules/{rule.Id}", rule);
    }

    static async Task<IResult> Update(ArchiveDbContext db, int id, RuleIn body)
    {
        var rule = await db.RoutingRules
            .Include(r => r.RuleDestinations)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return Results.NotFound();
        if (body.DestinationIds.Count == 0)
            return Results.BadRequest("At least one destination is required");

        rule.Name             = body.Name;
        rule.Priority         = body.Priority;
        rule.Enabled          = body.Enabled;
        rule.MatchModality    = Normalise(body.MatchModality);
        rule.MatchAeTitle     = Normalise(body.MatchAeTitle);
        rule.MatchReceivingAe = Normalise(body.MatchReceivingAe);
        rule.MatchBodyPart    = Normalise(body.MatchBodyPart);
        rule.OnReceive        = body.OnReceive;
        rule.Description      = body.Description;
        rule.UpdatedAt        = DateTime.UtcNow;

        // Replace destinations
        db.RuleDestinations.RemoveRange(rule.RuleDestinations);
        foreach (var destId in body.DestinationIds)
            db.RuleDestinations.Add(new RuleDestination { RuleId = rule.Id, DestinationId = destId });

        await db.SaveChangesAsync();
        return Results.Ok(rule);
    }

    static async Task<IResult> Delete(ArchiveDbContext db, int id)
    {
        var rule = await db.RoutingRules.FindAsync(id);
        if (rule is null) return Results.NotFound();
        db.RoutingRules.Remove(rule);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    static async Task<IResult> RoutingLog(ArchiveDbContext db, int limit = 50)
    {
        var log = await db.RoutingLog
            .Include(rl => rl.Instance)
            .Include(rl => rl.Destination)
            .Include(rl => rl.Rule)
            .OrderByDescending(rl => rl.QueuedAt)
            .Take(Math.Min(limit, 200))
            .Select(rl => new {
                rl.Id, rl.Status, rl.Attempts, rl.LastError,
                rl.QueuedAt, rl.SentAt,
                InstanceUid     = rl.Instance != null ? rl.Instance.InstanceUid : null,
                DestinationName = rl.Destination != null ? rl.Destination.Name  : null,
                RuleName        = rl.Rule        != null ? rl.Rule.Name          : "Manual",
            })
            .ToListAsync();
        return Results.Ok(log);
    }

    private static string? Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpper();
}
