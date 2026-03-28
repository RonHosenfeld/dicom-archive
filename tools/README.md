# Tools — Testing & Development Utilities

This directory contains tools for testing the DICOM Archive system, from sending synthetic exams to verifying end-to-end remote routing across multiple agents.

---

## Contents

| Tool | Purpose |
|------|---------|
| `send_dicom.py` | Generate and send synthetic screening mammography exams via C-STORE |
| `test-pacs/` | Minimal DICOM SCP sink — accepts any C-STORE and logs metadata (no storage) |

Both tools are used standalone or as part of the Aspire multi-agent environment described below.

---

## send_dicom.py — Synthetic Exam Sender

Generates realistic screening mammography exams (4 views per exam: L-CC, L-MLO, R-CC, R-MLO) with randomized patient demographics, and sends them via DICOM C-STORE.

### Prerequisites

```bash
cd tools
python3 -m venv .venv && source .venv/bin/activate
pip install pydicom pynetdicom numpy
```

### Usage

All commands assume you are in the `tools/` directory with the venv activated.

```bash
# Send 1 exam to a local agent
python send_dicom.py ARCHIVE_SCP --host localhost --port 11112

# Send 10 exams
python send_dicom.py ARCHIVE_SCP --host localhost --port 11112 --count 10

# Reproducible data with a fixed seed
python send_dicom.py ARCHIVE_SCP --count 3 --seed 42

# Specify the calling AE title (default: TEST_SCU)
python send_dicom.py ARCHIVE_SCP --calling-ae MAMMO_UNIT_1
```

### Options

| Flag | Default | Description |
|------|---------|-------------|
| `ae_title` | *(required)* | Target AE title |
| `--host` | `localhost` | Target host |
| `--port` | `11112` | Target port |
| `--count` | `1` | Number of exams to generate and send |
| `--calling-ae` | `TEST_SCU` | Calling AE title presented to the SCP |
| `--seed` | *(random)* | Random seed for reproducible patient data |

Each exam produces 4 images (one per mammography view) with 64x64 16-bit synthetic pixel data.

---

## test-pacs/ — Lightweight DICOM Sink

A minimal Python DICOM SCP that accepts any C-STORE and logs the metadata to stdout. It does **not** save files or write to a database — it simply confirms receipt and logs what arrived. Useful as a routing destination to verify that images are being forwarded correctly.

### Standalone usage

The test PACS shares the same dependencies as `send_dicom.py` (pydicom, pynetdicom), so if you already have the `tools/.venv` set up you can run it directly. Otherwise:

```bash
cd tools
python3 -m venv .venv && source .venv/bin/activate
pip install -r test-pacs/requirements.txt
```

Start the test PACS (all commands run from the `tools/` directory):

```bash
# Use a port not already claimed by Aspire agents (11112-11115) or test PACS (10104-10106)
AE_TITLE=MY_TEST_PACS LISTEN_PORT=11117 python test-pacs/test_pacs.py
```

### Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `AE_TITLE` | `TEST_PACS` | AE title this SCP presents |
| `LISTEN_PORT` | `104` | Port to listen on |

### Docker

```bash
docker build -t test-pacs tools/test-pacs
docker run --rm -p 11117:104 -e AE_TITLE=MY_TEST_PACS test-pacs
```

### Example log output

```
2026-03-28 14:32:01 | INFO    | C-STORE #1 | Patient: Smith^Maria (SIM-0001) | Study: 1.2.840... | SOP: 1.2.840... | Modality: MG
2026-03-28 14:32:01 | INFO    | C-STORE #2 | Patient: Smith^Maria (SIM-0001) | Study: 1.2.840... | SOP: 1.2.840... | Modality: MG
```

### Sending a test to verify

With the test PACS running on port 11117, open a second terminal:

```bash
cd tools
source .venv/bin/activate

# C-ECHO (requires dcmtk)
echoscu -aec MY_TEST_PACS localhost 11117

# Send a synthetic exam
python send_dicom.py MY_TEST_PACS --port 11117
```

---

## Aspire Multi-Agent Environment

The Aspire AppHost (`aspire/DicomArchive.AppHost/`) defines a full multi-agent topology for testing remote routing. Starting it launches all of the following:

### Services

| Service | Type | Host Port | AE Title | Description |
|---------|------|-----------|----------|-------------|
| postgres | Database | — | — | Shared metadata store |
| azure-storage | Blob (Azurite) | — | — | Emulated blob storage |
| service-bus | Service Bus emulator | — | — | Message bus for remote routing |
| seq | Logging | — | — | Structured log viewer |
| dicom-server | .NET API | 8080 | — | REST API, web UI, routing engine |
| dicom-agent | Python agent | 11112 | `ARCHIVE_SCP` | Primary ingest agent |
| dicom-agent-remote-1 | Python agent | 11113 | `REMOTE_AGENT_1` | Remote agent 1 (pull engine enabled) |
| dicom-agent-remote-2 | Python agent | 11114 | `REMOTE_AGENT_2` | Remote agent 2 (pull engine enabled) |
| dicom-agent-remote-3 | Python agent | 11115 | `REMOTE_AGENT_3` | Remote agent 3 (pull engine enabled) |
| test-pacs-1 | DICOM sink | 10104 | `TEST_PACS_1` | Routing target for remote agent 1 |
| test-pacs-2 | DICOM sink | 10105 | `TEST_PACS_2` | Routing target for remote agent 2 |
| test-pacs-3 | DICOM sink | 10106 | `TEST_PACS_3` | Routing target for remote agent 3 |

### Architecture

```
storescu → ARCHIVE_SCP (:11112) → server → Service Bus
                                              ├── REMOTE_AGENT_1 (:11113) → TEST_PACS_1 (:10104)
                                              ├── REMOTE_AGENT_2 (:11114) → TEST_PACS_2 (:10105)
                                              └── REMOTE_AGENT_3 (:11115) → TEST_PACS_3 (:10106)
```

The primary agent (`ARCHIVE_SCP`) receives images and uploads them to the server. When routing rules match, the server publishes messages to Service Bus. Each remote agent has its pull engine enabled — it subscribes to Service Bus, downloads the instances from blob storage, and forwards them via C-STORE to its paired test PACS.

### Starting the environment

```bash
# Requires .NET 10 SDK + Aspire workload
dotnet run --project aspire/DicomArchive.AppHost
```

The Aspire dashboard opens automatically and shows all services. Wait for everything to reach a healthy/running state before testing.

---

## End-to-End Remote Routing Walkthrough

This walkthrough verifies that images sent to the primary agent are fanned out to all three remote agents via Service Bus and forwarded to their test PACS destinations.

### 1. Start the environment

```bash
dotnet run --project aspire/DicomArchive.AppHost
```

### 2. Verify all agents registered

```bash
curl -s http://localhost:8080/api/agents | python -m json.tool
```

You should see four agents: `ARCHIVE_SCP`, `REMOTE_AGENT_1`, `REMOTE_AGENT_2`, `REMOTE_AGENT_3`.

### 3. Create remote destinations

Open the web UI at **http://localhost:8080** → **Destinations** → **+ Add Destination** and create three destinations:

| Name | AE Title | Host | Port | Mode | Remote Agent AE |
|------|----------|------|------|------|-----------------|
| Test PACS 1 | `TEST_PACS_1` | `test-pacs-1` | `104` | Remote | `REMOTE_AGENT_1` |
| Test PACS 2 | `TEST_PACS_2` | `test-pacs-2` | `104` | Remote | `REMOTE_AGENT_2` |
| Test PACS 3 | `TEST_PACS_3` | `test-pacs-3` | `104` | Remote | `REMOTE_AGENT_3` |

The **Host** uses the Aspire container name, which resolves within the Docker network.

### 4. Create a fan-out routing rule

Go to **Rules** → **+ Add Rule**:

- **Name:** `Fan-out to all remote sites`
- **Destinations:** check all 3 test PACS destinations
- **Auto-route on receipt:** ON
- Leave all match criteria blank (matches everything)

### 5. Send test data

From the `tools/` directory with the venv activated:

```bash
python send_dicom.py ARCHIVE_SCP --host localhost --port 11112 --count 1
```

This sends one 4-view mammography exam to the primary agent.

### 6. Observe the fan-out

What happens after sending:

1. **Primary agent** receives 4 images → uploads to server via blob storage
2. **Server** evaluates routing rules → publishes 3 Service Bus messages (one per remote destination)
3. **Each remote agent** picks up its message → downloads instances from blob storage → C-STOREs to its test PACS
4. **Each test PACS** logs 4 received instances

### 7. Verify

**Route log** — check that all deliveries completed:

```bash
curl -s http://localhost:8080/api/remote-routing-log | python -m json.tool
```

Look for 3 entries, all with `status: "delivered"`.

**Test PACS logs** — visible in the Aspire dashboard or via Docker:

```bash
# In Aspire, click on test-pacs-1/2/3 to see their console logs
# Each should show 4 C-STORE entries
```

**Studies** — confirm the exam is indexed:

```bash
curl -s http://localhost:8080/api/studies | python -m json.tool
```

### Troubleshooting

| Symptom | Check |
|---------|-------|
| Remote agents not appearing in `/api/agents` | Agents may still be starting — wait 30s and retry. Check Aspire dashboard for errors. |
| Route log shows `queued` but not `delivered` | Remote agent may not have created its Service Bus subscription yet. The pull engine retries every 30s. |
| Test PACS logs show no received images | Verify the destination host matches the Aspire container name (`test-pacs-1`, not `localhost`). Check that the destination port is `104` (container-internal). |
| Service Bus connection errors in agent logs | The emulator hostname is injected by Aspire. Check that `SERVICE_BUS_CONNECTION_STRING` is set in the agent's environment (visible in Aspire dashboard). |

---

## Ports Quick Reference

| Port | Service | Protocol |
|------|---------|----------|
| 8080 | Web UI / REST API | HTTP |
| 11112 | Primary agent (`ARCHIVE_SCP`) | DICOM |
| 11113 | Remote agent 1 (`REMOTE_AGENT_1`) | DICOM |
| 11114 | Remote agent 2 (`REMOTE_AGENT_2`) | DICOM |
| 11115 | Remote agent 3 (`REMOTE_AGENT_3`) | DICOM |
| 10104 | Test PACS 1 (`TEST_PACS_1`) | DICOM |
| 10105 | Test PACS 2 (`TEST_PACS_2`) | DICOM |
| 10106 | Test PACS 3 (`TEST_PACS_3`) | DICOM |
