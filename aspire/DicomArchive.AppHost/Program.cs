var builder = DistributedApplication.CreateBuilder(args);

// ── Postgres ──────────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("dicom-pgdata")
    .WithPgAdmin()
    .AddDatabase("dicom-archive");

// ── .NET Server ───────────────────────────────────────────────────────────────
var server = builder.AddProject<Projects.DicomArchive_Server>("dicom-server")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithHttpEndpoint(port: 8080, name: "web");

// ── Python Ingest Agent ───────────────────────────────────────────────────────
// Built from agent/Dockerfile. Aspire injects DATABASE_URL via WithReference;
// other env vars are set explicitly.
builder.AddDockerfile("dicom-agent", "../../agent")
    .WithReference(postgres)          // injects ConnectionStrings__dicom-archive
    .WithEnvironment("STORAGE_BACKEND",   "local")
    .WithEnvironment("LOCAL_STORAGE_PATH", "/data/received")
    .WithEnvironment("AE_TITLE",          "ARCHIVE_SCP")
    .WithEnvironment("LISTEN_PORT",       "11112")
    .WithEnvironment("ROUTER_URL",        server.GetEndpoint("web"))
    .WithBindMount("../../data/received",   "/data/received")
    .WithBindMount("../../data/quarantine", "/data/quarantine")
    .WithEndpoint(port: 11112, targetPort: 11112, scheme: "tcp", name: "dicom")
    .WaitFor(server);

builder.Build().Run();
