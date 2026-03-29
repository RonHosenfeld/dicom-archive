var builder = DistributedApplication.CreateBuilder(args);

// ── Shared secret for agent ↔ server auth ───────────────────────────────────
// In production, use a proper secret store. For local dev, generate one.
var agentApiKey = builder.AddParameter("agent-api-key", secret: true);

// ── Postgres ──────────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin()
    .AddDatabase("dicom-archive");

// ── Azure Storage (Azurite emulator for local dev) ─────────────────────────
var storage = builder.AddAzureStorage("azure-storage")
    .RunAsEmulator(emulator =>
        emulator.WithArgs("--disableProductStyleUrl"));  // path-style URLs for Docker networking
var blobs = storage.AddBlobs("blobs");

// ── Azure Service Bus (emulator for local dev) ──────────────────────────────
var serviceBus = builder.AddAzureServiceBus("service-bus")
    .RunAsEmulator();
serviceBus.AddServiceBusTopic("routed-exams");

// ── Seq (structured logging) ─────────────────────────────────────────────────
var seq = builder.AddSeq("seq")
    .ExcludeFromManifest();                 // ephemeral — no volume, resets each run

// ── .NET Server ───────────────────────────────────────────────────────────────
var server = builder.AddProject<Projects.DicomArchive_Server>("dicom-server")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithReference(blobs)
    .WaitFor(storage)
    .WithReference(serviceBus)
    .WaitFor(serviceBus)
    .WithReference(seq)
    .WithEnvironment("STORAGE_BACKEND",  "azure")
    .WithEnvironment("AZURE_CONTAINER",  "dicom-files")
    .WithEnvironment("AGENT_API_KEY",    agentApiKey)
    .WithHttpEndpoint(port: 8080, name: "web");

// ── Python Ingest Agent ───────────────────────────────────────────────────────
// The agent has NO database access and NO cloud storage credentials.
// It communicates with the server via the 3-step ingest handshake and
// uploads files directly to blob storage using pre-signed URLs from the server.
builder.AddDockerfile("dicom-agent", "../../agent")
    .WaitFor(server)
    .WaitFor(storage)
    .WithEnvironment("AE_TITLE",           "ARCHIVE_SCP")
    .WithEnvironment("LISTEN_PORT",        "11112")
    .WithEnvironment("SERVER_URL",         server.GetEndpoint("web"))
    .WithEnvironment("AGENT_API_KEY",      agentApiKey)
    .WithEnvironment("UPLOAD_WORKERS",     "4")
    .WithEnvironment("STAGING_PATH",       "/data/staging")
    .WithEnvironment("SEQ_URL",            seq.GetEndpoint("http"))
    .WithBindMount("../../data/staging",    "/data/staging")
    .WithBindMount("../../data/quarantine", "/data/quarantine")
    .WithEndpoint(port: 11112, targetPort: 11112, scheme: "tcp", name: "dicom");

// ── Remote Agents (pull from Service Bus, forward to test PACS) ─────────────
for (int i = 1; i <= 3; i++)
{
    var agentName = $"dicom-agent-remote-{i}";
    var aeTitle = $"REMOTE_AGENT_{i}";
    var hostPort = 11112 + i; // 11113, 11114, 11115

    builder.AddDockerfile(agentName, "../../agent")
        .WaitFor(server)
        .WaitFor(storage)
        .WaitFor(serviceBus)
        .WithEnvironment("AE_TITLE",                     aeTitle)
        .WithEnvironment("LISTEN_PORT",                   hostPort.ToString())
        .WithEnvironment("SERVER_URL",                    server.GetEndpoint("web"))
        .WithEnvironment("AGENT_API_KEY",                 agentApiKey)
        .WithEnvironment("UPLOAD_WORKERS",                "2")
        .WithEnvironment("STAGING_PATH",                  "/data/staging")
        .WithEnvironment("REMOTE_ROUTING_ENABLED",        "true")
        .WithEnvironment("SERVICE_BUS_CONNECTION_STRING", serviceBus.Resource.ConnectionStringExpression)
        .WithEnvironment("PULL_WORKERS",                  "2")
        .WithEnvironment("SEQ_URL",                       seq.GetEndpoint("http"))
        .WithBindMount($"../../data/remote-{i}-staging",    "/data/staging")
        .WithBindMount($"../../data/remote-{i}-quarantine", "/data/quarantine")
        .WithEndpoint(port: hostPort, targetPort: hostPort, scheme: "tcp", name: "dicom");
}

// ── Test PACS (lightweight DICOM sinks for verifying remote routing) ────────
var testPacsList = new List<(string Name, string AeTitle, IResourceBuilder<ContainerResource> Resource)>();
for (int i = 1; i <= 3; i++)
{
    var pacsName = $"test-pacs-{i}";
    var aeTitle = $"TEST_PACS_{i}";
    var dicomHostPort = 10103 + i; // 10104, 10105, 10106
    var httpHostPort = 18080 + i;  // 18081, 18082, 18083

    var pacs = builder.AddDockerfile(pacsName, "../../tools/test-pacs")
        .WithEnvironment("AE_TITLE",     aeTitle)
        .WithEnvironment("LISTEN_PORT",  "104")
        .WithEnvironment("HTTP_PORT",    "8080")
        .WithEndpoint(port: dicomHostPort, targetPort: 104, scheme: "tcp", name: "dicom")
        .WithHttpEndpoint(port: httpHostPort, targetPort: 8080, name: "http");

    testPacsList.Add((pacsName, aeTitle, pacs));
}

// Inject test-pacs URLs into the server so it can proxy browser requests
for (int i = 0; i < testPacsList.Count; i++)
{
    var (name, aeTitle, pacs) = testPacsList[i];
    var idx = i + 1;
    var dicomPort = 10103 + idx; // matches dicomHostPort above
    server
        .WithEnvironment($"TEST_PACS_{idx}_URL", pacs.GetEndpoint("http"))
        .WithEnvironment($"TEST_PACS_{idx}_AE", aeTitle)
        .WithEnvironment($"TEST_PACS_{idx}_PORT", dicomPort.ToString());
}

builder.Build().Run();
