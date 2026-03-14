var builder = DistributedApplication.CreateBuilder(args);

// ── Postgres ──────────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin()
    .AddDatabase("dicom-archive");

// ── Azure Storage (Azurite emulator for local dev) ─────────────────────────
var storage = builder.AddAzureStorage("azure-storage")
    .RunAsEmulator(emulator =>
        emulator.WithArgs("--disableProductStyleUrl"));  // path-style URLs for Docker networking
var blobs = storage.AddBlobs("blobs");

// ── Seq (structured logging) ─────────────────────────────────────────────────
var seq = builder.AddSeq("seq")
    .ExcludeFromManifest();                 // ephemeral — no volume, resets each run

// ── .NET Server ───────────────────────────────────────────────────────────────
var server = builder.AddProject<Projects.DicomArchive_Server>("dicom-server")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithReference(blobs)
    .WaitFor(storage)
    .WithReference(seq)
    .WithEnvironment("STORAGE_BACKEND",  "azure")
    .WithEnvironment("AZURE_CONTAINER",  "dicom-files")
    .WithHttpEndpoint(port: 8080, name: "web");

// ── Python Ingest Agent ───────────────────────────────────────────────────────
// WithReference(postgres) injects ConnectionStrings__dicom-archive in ADO.NET
// format (Host=...;Database=...;Username=...;Password=...).
// The Python agent reads this and converts it to a psycopg2 URL — see database.py.
builder.AddDockerfile("dicom-agent", "../../agent")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WaitFor(storage)
    .WithEnvironment("STORAGE_BACKEND",    "azure")
    .WithEnvironment("AZURE_CONTAINER",    "dicom-files")
    .WithEnvironment(ctx =>
    {
        ctx.EnvironmentVariables["AZURE_STORAGE_CONNECTION_STRING"] =
            blobs.Resource.ConnectionStringExpression;
    })
    .WithEnvironment("AE_TITLE",           "ARCHIVE_SCP")
    .WithEnvironment("LISTEN_PORT",        "11112")
    .WithEnvironment("ROUTER_URL",         server.GetEndpoint("web"))
    .WithEnvironment("SEQ_URL",            seq.GetEndpoint("http"))
    .WithBindMount("../../data/quarantine", "/data/quarantine")
    .WithEndpoint(port: 11112, targetPort: 11112, scheme: "tcp", name: "dicom")
    .WaitFor(server);

builder.Build().Run();
