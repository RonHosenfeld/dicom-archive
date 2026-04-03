using System.IO.Compression;
using DicomArchive.Server.Data;
using DicomArchive.Server.Services;
using FellowOakDicom;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

public static class StudyEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/studies", ListStudies);
        app.MapGet("/api/studies/{studyUid}", GetStudy);
        app.MapGet("/api/studies/{studyUid}/series", GetStudySeries);
        app.MapGet("/api/series/{seriesUid}/instances", GetSeriesInstances);
        app.MapGet("/api/instances/{instanceUid}", GetInstance);
        app.MapGet("/api/instances/{instanceUid}/file", DownloadInstance);
        app.MapGet("/api/studies/{studyUid}/download", DownloadStudy);
        app.MapGet("/api/stats", GetStats);
    }

    static async Task<IResult> ListStudies(
        ArchiveDbContext db,
        string? search = null, string? modality = null,
        string? date_from = null, string? date_to = null,
        int limit = 100, int offset = 0)
    {
        var q = db.Exams
            .Include(e => e.Patient)
            .Include(e => e.SeriesList).ThenInclude(s => s.Instances)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            q = q.Where(e =>
                EF.Functions.ILike(e.Patient.PatientId, pattern) ||
                (e.Patient.Name != null && EF.Functions.ILike(e.Patient.Name, pattern)) ||
                (e.Accession != null && EF.Functions.ILike(e.Accession, pattern)) ||
                (e.Description != null && EF.Functions.ILike(e.Description, pattern))
            );
        }
        if (!string.IsNullOrEmpty(modality))
            q = q.Where(e => e.Modality == modality.ToUpper());
        if (DateOnly.TryParse(date_from, out var df))
            q = q.Where(e => e.StudyDate >= df);
        if (DateOnly.TryParse(date_to, out var dt))
            q = q.Where(e => e.StudyDate <= dt);

        var results = await q
            .OrderByDescending(e => e.StudyDate)
            .ThenByDescending(e => e.Id)
            .Skip(offset).Take(limit)
            .Select(e => new StudySummary(
                e.Id, e.StudyUid, e.StudyDate, e.Accession,
                e.Description, e.Modality,
                e.Patient.PatientId, e.Patient.Name, e.Patient.BirthDate,
                e.SeriesList.Count,
                e.SeriesList.Sum(s => s.Instances.Count)
            ))
            .ToListAsync();

        return Results.Ok(results);
    }

    static async Task<IResult> GetStudy(ArchiveDbContext db, string studyUid)
    {
        var exam = await db.Exams
            .Include(e => e.Patient)
            .Where(e => e.StudyUid == studyUid)
            .Select(e => new {
                e.Id, e.StudyUid, e.StudyDate, e.StudyTime, e.Accession,
                e.Description, e.Modality, e.ReferringPhysician,
                Patient = new { e.Patient.PatientId, e.Patient.Name, e.Patient.BirthDate, e.Patient.Sex },
            })
            .FirstOrDefaultAsync();
        return exam is null ? Results.NotFound() : Results.Ok(exam);
    }

    static async Task<IResult> GetStudySeries(ArchiveDbContext db, string studyUid)
    {
        var series = await db.Series
            .Include(s => s.Instances)
            .Where(s => s.Exam.StudyUid == studyUid)
            .OrderBy(s => s.SeriesNumber)
            .Select(s => new {
                s.Id, s.SeriesUid, s.SeriesNumber, s.SeriesDate,
                s.BodyPart, s.Description, s.Laterality, s.ViewPosition,
                InstanceCount = s.Instances.Count
            })
            .ToListAsync();
        return Results.Ok(series);
    }

    static async Task<IResult> GetSeriesInstances(ArchiveDbContext db, string seriesUid)
    {
        var instances = await db.Instances
            .Where(i => i.Series.SeriesUid == seriesUid)
            .OrderBy(i => i.InstanceNumber)
            .ToListAsync();
        return Results.Ok(instances);
    }

    static async Task<IResult> GetInstance(ArchiveDbContext db, string instanceUid)
    {
        var inst = await db.Instances
            .Include(i => i.Series).ThenInclude(s => s.Exam)
                .ThenInclude(e => e.Patient)
            .FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);
        return inst is null ? Results.NotFound() : Results.Ok(inst);
    }

    static async Task<IResult> DownloadInstance(
        ArchiveDbContext db,
        DicomArchive.Server.Services.StorageService storage,
        string instanceUid)
    {
        var inst = await db.Instances.FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);
        if (inst is null) return Results.NotFound();

        var path = await storage.FetchToTempAsync(inst.BlobKey);
        var bytes = await File.ReadAllBytesAsync(path);
        File.Delete(path);

        return Results.File(bytes, "application/dicom", $"{instanceUid}.dcm");
    }

    static async Task DownloadStudy(
        HttpContext httpContext,
        ArchiveDbContext db,
        StorageService storage,
        string studyUid,
        bool anonymize = false)
    {
        var seriesList = await db.Series
            .Include(s => s.Instances)
            .Where(s => s.Exam.StudyUid == studyUid)
            .OrderBy(s => s.SeriesNumber)
            .ToListAsync();

        var allInstances = seriesList.SelectMany(s => s.Instances).ToList();
        if (allInstances.Count == 0)
        {
            httpContext.Response.StatusCode = 404;
            return;
        }

        // Allow sync IO for this request only — ZipArchive.Dispose writes the
        // central directory synchronously and there is no async ZipArchive API.
        // This lets us stream directly to the response instead of buffering the
        // entire ZIP in memory, which matters for large exams (100MB+).
        var syncIoFeature = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (syncIoFeature is not null)
            syncIoFeature.AllowSynchronousIO = true;

        httpContext.Response.ContentType = "application/zip";
        httpContext.Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{studyUid}.zip\"";

        // Stream the ZIP directly to the response body so the browser receives
        // bytes immediately and large exams don't consume server memory.
        using var zip = new ZipArchive(httpContext.Response.Body, ZipArchiveMode.Create, leaveOpen: true);

        var seriesIndex = 0;
        foreach (var series in seriesList)
        {
            seriesIndex++;
            var seriesFolder = $"Series_{seriesIndex:D3}";
            if (!string.IsNullOrWhiteSpace(series.Description))
                seriesFolder += $"_{SanitizeFileName(series.Description)}";

            var instanceIndex = 0;
            foreach (var instance in series.Instances.OrderBy(i => i.InstanceNumber))
            {
                instanceIndex++;
                string? tempPath = null;
                string? anonPath = null;
                try
                {
                    tempPath = await storage.FetchToTempAsync(instance.BlobKey);

                    var sourcePath = tempPath;
                    if (anonymize)
                    {
                        var dcmFile = await DicomFile.OpenAsync(tempPath);
                        AnonymizationService.Anonymize(dcmFile.Dataset);
                        anonPath = Path.Combine(Path.GetTempPath(), $"dcm_anon_{Guid.NewGuid():N}.dcm");
                        await dcmFile.SaveAsync(anonPath);
                        sourcePath = anonPath;
                    }

                    var entryName = $"{seriesFolder}/IMG_{instanceIndex:D3}.dcm";
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await using var fileStream = File.OpenRead(sourcePath);
                    await fileStream.CopyToAsync(entryStream);
                }
                finally
                {
                    if (tempPath is not null && File.Exists(tempPath))
                        File.Delete(tempPath);
                    if (anonPath is not null && File.Exists(anonPath))
                        File.Delete(anonPath);
                }
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    static async Task<IResult> GetStats(ArchiveDbContext db)
    {
        var stats = new StatsResult(
            TotalPatients    : await db.Patients.LongCountAsync(),
            TotalStudies     : await db.Exams.LongCountAsync(),
            TotalSeries      : await db.Series.LongCountAsync(),
            TotalInstances   : await db.Instances.LongCountAsync(),
            TotalBytes       : await db.Instances.SumAsync(i => i.SizeBytes ?? 0),
            RoutesOk         : await db.RoutingLog.LongCountAsync(r => r.Status == "success"),
            RoutesFailed     : await db.RoutingLog.LongCountAsync(r => r.Status == "failed"),
            RoutesQueued     : await db.RoutingLog.LongCountAsync(r => r.Status == "queued"),
            RemotePublished  : await db.RemoteRoutingLog.LongCountAsync(r => r.Status == "published"),
            RemoteClaimed    : await db.RemoteRoutingLog.LongCountAsync(r => r.Status == "claimed"),
            RemoteDelivered  : await db.RemoteRoutingLog.LongCountAsync(r => r.Status == "delivered")
        );
        return Results.Ok(stats);
    }
}
