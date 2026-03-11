var builder = DistributedApplication.CreateBuilder(args);

// ── Postgres ──────────────────────────────────────────────────────────────────
// Aspire spins up a Postgres container, creates the database, and injects
// the connection string into all services that reference it.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("dicom-pgdata")         // persist DB across restarts
    .WithPgAdmin()                          // optional: PgAdmin UI on a random port
    .AddDatabase("dicom-archive");

// ── .NET Server ───────────────────────────────────────────────────────────────
// Full Aspire citizen — gets OpenTelemetry, health checks, and structured
// logs automatically via ServiceDefaults.
var server = builder.AddProject<Projects.DicomArchive_Server>("dicom-server")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithHttpEndpoint(port: 8080, name: "web");

// ── Python Ingest Agent ───────────────────────────────────────────────────────
// Built from the local Dockerfile in agent/.
// Aspire builds the image on first run and on changes to the Dockerfile.
var agent = builder.AddDockerfile("dicom-agent", "../../agent")
    .WithEnvironment("DATABASE_URL",
        postgres.Resource.GetConnectionString("dicom-archive")
            .Replace("Host=", "host=")        // translate Aspire format → psycopg2 URL
            ?? "")
    .WithEnvironment("STORAGE_BACKEND",  "local")
    .WithEnvironment("LOCAL_STORAGE_PATH", "/data/received")
    .WithEnvironment("ROUTER_URL",
        server.GetEndpoint("web").Url)
    .WithEnvironment("AE_TITLE",         "ARCHIVE_SCP")
    .WithEnvironment("LISTEN_PORT",      "11112")
    .WithBindMount("../../data/received",    "/data/received")
    .WithBindMount("../../data/quarantine",  "/data/quarantine")
    .WithEndpoint(port: 11112, targetPort: 11112, scheme: "tcp", name: "dicom")
    .WaitFor(server);

builder.Build().Run();
