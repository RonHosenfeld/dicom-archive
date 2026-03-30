using System.Data;
using DicomArchive.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

public static class MetricsEndpoints
{
    private static readonly HashSet<string> AllowedBuckets = new() { "hour", "day" };

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/metrics");

        group.MapGet("/summary", GetSummary);
        group.MapGet("/ingest", GetIngest);
        group.MapGet("/storage", GetStorage);
        group.MapGet("/routing", GetRouting);
    }

    private static async Task<IResult> GetSummary(ArchiveDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
              (SELECT COUNT(*) FROM exams WHERE created_at >= NOW() - interval '1 day') AS exams_today,
              (SELECT COUNT(*) FROM exams WHERE created_at >= NOW() - interval '7 days') AS exams_7d,
              (SELECT COUNT(*) FROM exams WHERE created_at >= NOW() - interval '30 days') AS exams_30d,
              (SELECT COUNT(*) FROM instances WHERE received_at >= NOW() - interval '1 day') AS instances_today,
              (SELECT COUNT(*) FROM instances WHERE received_at >= NOW() - interval '7 days') AS instances_7d,
              (SELECT COUNT(*) FROM instances WHERE received_at >= NOW() - interval '30 days') AS instances_30d,
              (SELECT COALESCE(SUM(size_bytes),0) FROM instances WHERE received_at >= NOW() - interval '1 day') AS bytes_today,
              (SELECT COALESCE(SUM(size_bytes),0) FROM instances WHERE received_at >= NOW() - interval '7 days') AS bytes_7d,
              (SELECT COALESCE(SUM(size_bytes),0) FROM instances WHERE received_at >= NOW() - interval '30 days') AS bytes_30d,
              (SELECT COUNT(*) FROM routing_log WHERE status='success' AND queued_at >= NOW() - interval '30 days') AS routes_ok_30d,
              (SELECT COUNT(*) FROM routing_log WHERE status='failed' AND queued_at >= NOW() - interval '30 days') AS routes_failed_30d";

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        return Results.Ok(new MetricsSummary(
            ExamsToday: reader.GetInt64(0),
            Exams7d: reader.GetInt64(1),
            Exams30d: reader.GetInt64(2),
            InstancesToday: reader.GetInt64(3),
            Instances7d: reader.GetInt64(4),
            Instances30d: reader.GetInt64(5),
            BytesToday: reader.GetInt64(6),
            Bytes7d: reader.GetInt64(7),
            Bytes30d: reader.GetInt64(8),
            RoutesOk30d: reader.GetInt64(9),
            RoutesFailed30d: reader.GetInt64(10)
        ));
    }

    private static async Task<IResult> GetIngest(ArchiveDbContext db, int days = 30, string bucket = "day")
    {
        if (!AllowedBuckets.Contains(bucket)) bucket = "day";
        if (days < 1 || days > 365) days = 30;

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT date_trunc('{bucket}', i.received_at) AS period,
                   COUNT(DISTINCT e.id) AS exams,
                   COUNT(DISTINCT s.id) AS series,
                   COUNT(*)             AS instances,
                   COALESCE(SUM(i.size_bytes), 0) AS bytes
            FROM instances i
            JOIN series s ON s.id = i.series_id
            JOIN exams e  ON e.id = s.exam_id
            WHERE i.received_at >= NOW() - make_interval(days => @days)
            GROUP BY period ORDER BY period";

        var p = cmd.CreateParameter();
        p.ParameterName = "days";
        p.Value = days;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<IngestBucket>();
        while (await reader.ReadAsync())
        {
            results.Add(new IngestBucket(
                Period: reader.GetDateTime(0),
                Exams: reader.GetInt64(1),
                Series: reader.GetInt64(2),
                Instances: reader.GetInt64(3),
                Bytes: reader.GetInt64(4)
            ));
        }
        return Results.Ok(results);
    }

    private static async Task<IResult> GetStorage(ArchiveDbContext db, int days = 90, string bucket = "day")
    {
        if (!AllowedBuckets.Contains(bucket)) bucket = "day";
        if (days < 1 || days > 365) days = 90;

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        // Get base_bytes (total before window)
        await using var baseCmd = conn.CreateCommand();
        baseCmd.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM instances WHERE received_at < NOW() - make_interval(days => @days)";
        var bp = baseCmd.CreateParameter();
        bp.ParameterName = "days";
        bp.Value = days;
        baseCmd.Parameters.Add(bp);
        var baseBytes = Convert.ToInt64(await baseCmd.ExecuteScalarAsync());

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH buckets AS (
              SELECT date_trunc('{bucket}', received_at) AS period,
                     COUNT(*) AS instances_added,
                     COALESCE(SUM(size_bytes), 0) AS bytes_added
              FROM instances
              WHERE received_at >= NOW() - make_interval(days => @days)
              GROUP BY period
            )
            SELECT period, instances_added, bytes_added,
                   SUM(bytes_added) OVER (ORDER BY period) AS cumulative_bytes
            FROM buckets ORDER BY period";

        var p = cmd.CreateParameter();
        p.ParameterName = "days";
        p.Value = days;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        var buckets = new List<StorageBucket>();
        while (await reader.ReadAsync())
        {
            buckets.Add(new StorageBucket(
                Period: reader.GetDateTime(0),
                InstancesAdded: reader.GetInt64(1),
                BytesAdded: reader.GetInt64(2),
                CumulativeBytes: reader.GetInt64(3)
            ));
        }
        return Results.Ok(new StorageMetrics(baseBytes, buckets));
    }

    private static async Task<IResult> GetRouting(ArchiveDbContext db, int days = 30, string bucket = "day")
    {
        if (!AllowedBuckets.Contains(bucket)) bucket = "day";
        if (days < 1 || days > 365) days = 30;

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT date_trunc('{bucket}', queued_at) AS period,
                   COUNT(*) FILTER (WHERE status='success') AS success,
                   COUNT(*) FILTER (WHERE status='failed')  AS failed,
                   COUNT(*) FILTER (WHERE status='queued')   AS queued,
                   AVG(EXTRACT(EPOCH FROM (sent_at - queued_at)))
                       FILTER (WHERE status='success' AND sent_at IS NOT NULL) AS avg_latency_sec
            FROM routing_log
            WHERE queued_at >= NOW() - make_interval(days => @days)
            GROUP BY period ORDER BY period";

        var p = cmd.CreateParameter();
        p.ParameterName = "days";
        p.Value = days;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<RoutingBucket>();
        while (await reader.ReadAsync())
        {
            results.Add(new RoutingBucket(
                Period: reader.GetDateTime(0),
                Success: reader.GetInt64(1),
                Failed: reader.GetInt64(2),
                Queued: reader.GetInt64(3),
                AvgLatencySec: reader.IsDBNull(4) ? null : reader.GetDouble(4)
            ));
        }
        return Results.Ok(results);
    }
}
