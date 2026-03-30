using DicomArchive.Server.Data;
using DicomArchive.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

public static class DestinationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet   ("/api/destinations",          List);
        app.MapPost  ("/api/destinations",          Create).WithName("CreateDestination");
        app.MapGet   ("/api/destinations/{id:int}", Get);
        app.MapPut   ("/api/destinations/{id:int}", Update);
        app.MapDelete("/api/destinations/{id:int}", Delete);
        app.MapPost  ("/api/destinations/{id:int}/echo", Echo);
    }

    static async Task<IResult> List(ArchiveDbContext db) =>
        Results.Ok(await db.AeDestinations.OrderBy(d => d.Name).ToListAsync());

    static async Task<IResult> Get(ArchiveDbContext db, int id)
    {
        var d = await db.AeDestinations.FindAsync(id);
        return d is null ? Results.NotFound() : Results.Ok(d);
    }

    static async Task<IResult> Create(ArchiveDbContext db, DestinationIn body)
    {
        var dest = new AeDestination
        {
            Name          = body.Name,
            AeTitle       = body.AeTitle.ToUpper(),
            Host          = body.Host,
            Port          = body.Port,
            Description   = body.Description,
            Enabled       = body.Enabled,
            RoutingMode   = body.RoutingMode ?? "direct",
            RemoteAgentAe = body.RemoteAgentAe?.ToUpper(),
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        };
        db.AeDestinations.Add(dest);
        await db.SaveChangesAsync();
        return Results.Created($"/api/destinations/{dest.Id}", dest);
    }

    static async Task<IResult> Update(ArchiveDbContext db, int id, DestinationIn body)
    {
        var dest = await db.AeDestinations.FindAsync(id);
        if (dest is null) return Results.NotFound();

        dest.Name          = body.Name;
        dest.AeTitle       = body.AeTitle.ToUpper();
        dest.Host          = body.Host;
        dest.Port          = body.Port;
        dest.Description   = body.Description;
        dest.Enabled       = body.Enabled;
        dest.RoutingMode   = body.RoutingMode ?? "direct";
        dest.RemoteAgentAe = body.RemoteAgentAe?.ToUpper();
        dest.UpdatedAt     = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(dest);
    }

    static async Task<IResult> Delete(ArchiveDbContext db, int id)
    {
        var dest = await db.AeDestinations.FindAsync(id);
        if (dest is null) return Results.NotFound();
        db.AeDestinations.Remove(dest);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    static async Task<IResult> Echo(ArchiveDbContext db, RouterService router, int id)
    {
        var dest = await db.AeDestinations.FindAsync(id);
        if (dest is null) return Results.NotFound();
        var (ok, message) = await router.EchoAsync(dest.AeTitle, dest.Host, dest.Port);
        return Results.Ok(new { ok, message });
    }
}
