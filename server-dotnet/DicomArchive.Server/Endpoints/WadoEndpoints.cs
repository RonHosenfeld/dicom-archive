using DicomArchive.Server.Data;
using DicomArchive.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

/// <summary>
/// Minimal WADO-RS endpoints — enough for OHIF Viewer compatibility.
/// </summary>
public static class WadoEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(
            "/wado/studies/{studyUid}/series/{seriesUid}/instances/{instanceUid}",
            RetrieveInstance);

        app.MapGet(
            "/wado/studies/{studyUid}/series/{seriesUid}/instances/{instanceUid}/metadata",
            Metadata);

        app.MapGet("/api/series/{seriesUid}/viewer-urls", ViewerUrls);
        app.MapGet("/api/instances/{instanceUid}/blob-url", BlobUrl);
    }

    static async Task<IResult> RetrieveInstance(
        ArchiveDbContext db, StorageService storage,
        string studyUid, string seriesUid, string instanceUid)
    {
        var inst = await db.Instances
            .FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);
        if (inst is null) return Results.NotFound();

        var path = await storage.FetchToTempAsync(inst.BlobKey);
        var raw  = await File.ReadAllBytesAsync(path);
        File.Delete(path);

        const string boundary = "DICOMwebBoundary";
        var body = System.Text.Encoding.ASCII.GetBytes(
            $"--{boundary}\r\nContent-Type: application/dicom\r\n\r\n")
            .Concat(raw)
            .Concat(System.Text.Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n"))
            .ToArray();

        return Results.Bytes(body,
            $"multipart/related; type=application/dicom; boundary={boundary}");
    }

    static async Task<IResult> Metadata(
        ArchiveDbContext db,
        string studyUid, string seriesUid, string instanceUid)
    {
        var inst = await db.Instances
            .Include(i => i.Series).ThenInclude(s => s.Exam)
                .ThenInclude(e => e.Patient)
            .FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);

        return inst is null ? Results.NotFound() : Results.Ok(inst);
    }

    /// <summary>
    /// Returns viewer-ready URLs for all instances in a series.
    /// For cloud backends these are pre-signed blob URLs; for local storage,
    /// they fall back to the server-proxied /api/instances/{uid}/file endpoint.
    /// </summary>
    static async Task<IResult> ViewerUrls(
        ArchiveDbContext db, StorageService storage, string seriesUid)
    {
        var instances = await db.Instances
            .Where(i => i.Series.SeriesUid == seriesUid)
            .OrderBy(i => i.InstanceNumber)
            .ToListAsync();

        if (instances.Count == 0) return Results.NotFound();

        var urls = instances.Select(i =>
        {
            var (url, expiresAt) = storage.GenerateReadUrl(i.BlobKey, i.InstanceUid);
            return new
            {
                instanceUid    = i.InstanceUid,
                instanceNumber = i.InstanceNumber,
                url,
                rows           = i.Rows,
                columns        = i.Columns,
                expiresAt      = expiresAt.ToString("o"),
            };
        }).ToList();

        return Results.Ok(urls);
    }

    /// <summary>
    /// Returns a single blob URL for one instance (used for single-instance viewing).
    /// </summary>
    static async Task<IResult> BlobUrl(
        ArchiveDbContext db, StorageService storage, string instanceUid)
    {
        var inst = await db.Instances
            .FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);
        if (inst is null) return Results.NotFound();

        var (url, expiresAt) = storage.GenerateReadUrl(inst.BlobKey, inst.InstanceUid);
        return Results.Ok(new { url, expiresAt = expiresAt.ToString("o") });
    }
}
