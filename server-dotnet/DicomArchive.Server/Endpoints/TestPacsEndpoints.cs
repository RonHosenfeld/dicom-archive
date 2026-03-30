namespace DicomArchive.Server.Endpoints;

public static class TestPacsEndpoints
{
    private record TestPacsInstance(string Name, string AeTitle, string Url, int? DicomPort);
    private static List<TestPacsInstance>? _instances;

    private static List<TestPacsInstance> GetInstances()
    {
        if (_instances != null) return _instances;

        var list = new List<TestPacsInstance>();
        for (int i = 1; i <= 10; i++)
        {
            var url = Environment.GetEnvironmentVariable($"TEST_PACS_{i}_URL");
            var ae = Environment.GetEnvironmentVariable($"TEST_PACS_{i}_AE");
            if (string.IsNullOrEmpty(url)) break;
            var portStr = Environment.GetEnvironmentVariable($"TEST_PACS_{i}_PORT");
            int? port = int.TryParse(portStr, out var p) ? p : null;
            list.Add(new TestPacsInstance($"test-pacs-{i}", ae ?? $"TEST_PACS_{i}", url, port));
        }
        _instances = list;
        return list;
    }

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/test-pacs");

        group.MapGet("/", ListAll);
        group.MapGet("/{name}/received", GetReceived);
        group.MapPost("/{name}/clear", Clear);
    }

    private static async Task<IResult> ListAll(IHttpClientFactory httpFactory)
    {
        var instances = GetInstances();
        if (instances.Count == 0)
            return Results.Ok(Array.Empty<object>());

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);

        var tasks = instances.Select(async inst =>
        {
            try
            {
                var resp = await client.GetAsync($"{inst.Url}/status");
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                return new
                {
                    name = inst.Name,
                    ae_title = inst.AeTitle,
                    dicom_port = inst.DicomPort,
                    online = true,
                    total_received = json?.GetValueOrDefault("total_received"),
                    uptime_seconds = json?.GetValueOrDefault("uptime_seconds"),
                    buffer_size = json?.GetValueOrDefault("buffer_size"),
                };
            }
            catch
            {
                return new
                {
                    name = inst.Name,
                    ae_title = inst.AeTitle,
                    dicom_port = inst.DicomPort,
                    online = false,
                    total_received = (object?)null,
                    uptime_seconds = (object?)null,
                    buffer_size = (object?)null,
                };
            }
        });

        var results = await Task.WhenAll(tasks);
        return Results.Ok(results);
    }

    private static async Task<IResult> GetReceived(string name, IHttpClientFactory httpFactory, int? since_seq)
    {
        var inst = GetInstances().FirstOrDefault(i => i.Name == name);
        if (inst == null) return Results.NotFound(new { error = "Unknown test PACS" });

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);

        try
        {
            var url = $"{inst.Url}/received";
            if (since_seq.HasValue) url += $"?since_seq={since_seq.Value}";

            var resp = await client.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var content = await resp.Content.ReadAsStringAsync();
            return Results.Content(content, "application/json");
        }
        catch
        {
            return Results.StatusCode(502);
        }
    }

    private static async Task<IResult> Clear(string name, IHttpClientFactory httpFactory)
    {
        var inst = GetInstances().FirstOrDefault(i => i.Name == name);
        if (inst == null) return Results.NotFound(new { error = "Unknown test PACS" });

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(3);

        try
        {
            var resp = await client.PostAsync($"{inst.Url}/clear", null);
            resp.EnsureSuccessStatusCode();
            return Results.Ok(new { ok = true });
        }
        catch
        {
            return Results.StatusCode(502);
        }
    }
}
