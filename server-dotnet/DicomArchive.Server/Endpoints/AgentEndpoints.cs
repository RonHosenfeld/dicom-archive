using DicomArchive.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

public static class AgentEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet   ("/api/agents",                  List);
        app.MapGet   ("/api/agents/orphaned-rules",   OrphanedRules);
        app.MapGet   ("/api/agents/{id:int}",         Get);
        app.MapPatch ("/api/agents/{id:int}",         Update);
        app.MapDelete("/api/agents/{id:int}",         Delete);
    }

    static async Task<IResult> List(ArchiveDbContext db)
    {
        var agents = await db.Agents.OrderBy(a => a.AeTitle).ToListAsync();

        var onlineCutoff = DateTime.UtcNow.AddMinutes(-3);

        var ruleCountByAe = await db.RoutingRules
            .Where(r => r.MatchReceivingAe != null)
            .GroupBy(r => r.MatchReceivingAe!)
            .Select(g => new { AeTitle = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AeTitle, x => x.Count);

        var result = agents.Select(a => new
        {
            a.Id, a.AeTitle, a.Host, a.ListenPort, a.Description, a.Enabled,
            a.StorageBackend, a.Version,
            a.FirstSeen, a.LastSeen,
            a.InstancesReceived,
            Online    = a.LastSeen >= onlineCutoff,
            RuleCount = ruleCountByAe.GetValueOrDefault(a.AeTitle, 0),
        });

        return Results.Ok(result);
    }

    static async Task<IResult> OrphanedRules(ArchiveDbContext db)
    {
        var registeredAes = await db.Agents
            .Select(a => a.AeTitle)
            .ToListAsync();

        var orphans = await db.RoutingRules
            .Where(r => r.MatchReceivingAe != null
                     && !registeredAes.Contains(r.MatchReceivingAe))
            .Select(r => new { r.Id, r.Name, r.MatchReceivingAe })
            .ToListAsync();

        return Results.Ok(orphans);
    }

    static async Task<IResult> Get(ArchiveDbContext db, int id)
    {
        var agent = await db.Agents.FindAsync(id);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    static async Task<IResult> Update(ArchiveDbContext db, int id, AgentUpdate body)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return Results.NotFound();

        if (body.Description is not null) agent.Description = body.Description;
        if (body.Enabled is not null)     agent.Enabled     = body.Enabled.Value;
        await db.SaveChangesAsync();
        return Results.Ok(agent);
    }

    static async Task<IResult> Delete(ArchiveDbContext db, int id)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return Results.NotFound();
        db.Agents.Remove(agent);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
}
