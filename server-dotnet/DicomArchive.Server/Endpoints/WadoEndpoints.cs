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
}
