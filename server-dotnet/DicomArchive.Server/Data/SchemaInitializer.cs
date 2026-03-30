using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Data;

/// <summary>
/// Runs the DDL to create all tables on startup if they don't already exist.
/// Mirrors the schema in agent/database.py — all CREATE TABLE IF NOT EXISTS,
/// so it is safe to run against an existing database.
/// </summary>
public static class SchemaInitializer
{
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS patients (
            id          SERIAL PRIMARY KEY,
            patient_id  TEXT NOT NULL,
            name        TEXT,
            birth_date  DATE,
            sex         CHAR(1),
            created_at  TIMESTAMPTZ DEFAULT NOW(),
            UNIQUE (patient_id)
        );

        CREATE TABLE IF NOT EXISTS exams (
            id                  SERIAL PRIMARY KEY,
            patient_id          INTEGER NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
            study_uid           TEXT NOT NULL,
            study_date          DATE,
            study_time          TEXT,
            accession           TEXT,
            description         TEXT,
            modality            TEXT,
            referring_physician TEXT,
            created_at          TIMESTAMPTZ DEFAULT NOW(),
            UNIQUE (study_uid)
        );

        CREATE TABLE IF NOT EXISTS series (
            id            SERIAL PRIMARY KEY,
            exam_id       INTEGER NOT NULL REFERENCES exams(id) ON DELETE CASCADE,
            series_uid    TEXT NOT NULL,
            series_number INTEGER,
            series_date   DATE,
            body_part     TEXT,
            description   TEXT,
            laterality    TEXT,
            view_position TEXT,
            created_at    TIMESTAMPTZ DEFAULT NOW(),
            UNIQUE (series_uid)
        );

        CREATE TABLE IF NOT EXISTS instances (
            id              SERIAL PRIMARY KEY,
            series_id       INTEGER NOT NULL REFERENCES series(id) ON DELETE CASCADE,
            instance_uid    TEXT NOT NULL,
            instance_number INTEGER,
            blob_key        TEXT NOT NULL,
            blob_uri        TEXT,
            size_bytes      BIGINT,
            sha256          TEXT,
            transfer_syntax TEXT,
            rows            INTEGER,
            columns         INTEGER,
            received_at     TIMESTAMPTZ DEFAULT NOW(),
            sending_ae      TEXT,
            receiving_ae    TEXT,
            status          TEXT NOT NULL DEFAULT 'stored',
            UNIQUE (instance_uid)
        );

        CREATE TABLE IF NOT EXISTS ae_destinations (
            id          SERIAL PRIMARY KEY,
            name        TEXT NOT NULL,
            ae_title    TEXT NOT NULL,
            host        TEXT NOT NULL,
            port        INTEGER NOT NULL DEFAULT 104,
            description TEXT,
            enabled     BOOLEAN NOT NULL DEFAULT TRUE,
            created_at  TIMESTAMPTZ DEFAULT NOW(),
            updated_at  TIMESTAMPTZ DEFAULT NOW(),
            UNIQUE (name)
        );

        CREATE TABLE IF NOT EXISTS routing_rules (
            id                  SERIAL PRIMARY KEY,
            name                TEXT NOT NULL,
            priority            INTEGER NOT NULL DEFAULT 100,
            enabled             BOOLEAN NOT NULL DEFAULT TRUE,
            match_modality      TEXT,
            match_ae_title      TEXT,
            match_receiving_ae  TEXT,
            match_body_part     TEXT,
            on_receive          BOOLEAN NOT NULL DEFAULT FALSE,
            description         TEXT,
            created_at          TIMESTAMPTZ DEFAULT NOW(),
            updated_at          TIMESTAMPTZ DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS rule_destinations (
            rule_id        INTEGER NOT NULL REFERENCES routing_rules(id) ON DELETE CASCADE,
            destination_id INTEGER NOT NULL REFERENCES ae_destinations(id) ON DELETE CASCADE,
            PRIMARY KEY (rule_id, destination_id)
        );

        CREATE TABLE IF NOT EXISTS routing_log (
            id             SERIAL PRIMARY KEY,
            instance_id    INTEGER REFERENCES instances(id) ON DELETE SET NULL,
            rule_id        INTEGER REFERENCES routing_rules(id) ON DELETE SET NULL,
            destination_id INTEGER REFERENCES ae_destinations(id) ON DELETE SET NULL,
            status         TEXT NOT NULL DEFAULT 'queued',
            attempts       INTEGER NOT NULL DEFAULT 0,
            last_error     TEXT,
            queued_at      TIMESTAMPTZ DEFAULT NOW(),
            sent_at        TIMESTAMPTZ
        );

        CREATE TABLE IF NOT EXISTS agents (
            id                 SERIAL PRIMARY KEY,
            ae_title           TEXT NOT NULL,
            host               TEXT,
            description        TEXT,
            enabled            BOOLEAN NOT NULL DEFAULT TRUE,
            storage_backend    TEXT,
            version            TEXT,
            first_seen         TIMESTAMPTZ DEFAULT NOW(),
            last_seen          TIMESTAMPTZ DEFAULT NOW(),
            instances_received BIGINT NOT NULL DEFAULT 0,
            UNIQUE (ae_title)
        );

        -- Non-destructive migration: add status column to instances if upgrading
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='instances' AND column_name='status'
          ) THEN
            ALTER TABLE instances ADD COLUMN status TEXT NOT NULL DEFAULT 'stored';
          END IF;
        END $$;

        -- Non-destructive migration: add routing_mode + remote_agent_ae to ae_destinations
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='ae_destinations' AND column_name='routing_mode'
          ) THEN
            ALTER TABLE ae_destinations ADD COLUMN routing_mode TEXT NOT NULL DEFAULT 'direct';
            ALTER TABLE ae_destinations ADD COLUMN remote_agent_ae TEXT;
          END IF;
        END $$;

        -- Non-destructive migration: add service_bus_message_id + study_uid to routing_log
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='routing_log' AND column_name='service_bus_message_id'
          ) THEN
            ALTER TABLE routing_log ADD COLUMN service_bus_message_id TEXT;
            ALTER TABLE routing_log ADD COLUMN study_uid TEXT;
          END IF;
        END $$;

        -- Remote routing log (study-level tracking for Service Bus routing)
        CREATE TABLE IF NOT EXISTS remote_routing_log (
            id                     SERIAL PRIMARY KEY,
            study_uid              TEXT NOT NULL,
            rule_id                INTEGER REFERENCES routing_rules(id) ON DELETE SET NULL,
            destination_id         INTEGER REFERENCES ae_destinations(id) ON DELETE SET NULL,
            remote_agent_ae        TEXT,
            status                 TEXT NOT NULL DEFAULT 'published',
            service_bus_message_id TEXT,
            instance_count         INTEGER NOT NULL DEFAULT 0,
            instances_delivered    INTEGER NOT NULL DEFAULT 0,
            last_error             TEXT,
            published_at           TIMESTAMPTZ DEFAULT NOW(),
            completed_at           TIMESTAMPTZ
        );

        -- Non-destructive migration: add listen_port column to agents
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='agents' AND column_name='listen_port'
          ) THEN
            ALTER TABLE agents ADD COLUMN listen_port INTEGER;
          END IF;
        END $$;

        -- Non-destructive migration: add regex pattern columns to routing_rules
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='routing_rules' AND column_name='match_description_pattern'
          ) THEN
            ALTER TABLE routing_rules ADD COLUMN match_description_pattern TEXT;
            ALTER TABLE routing_rules ADD COLUMN match_referring_pattern TEXT;
          END IF;
        END $$;

        -- Trigram extension + indexes for fast ILIKE searches
        CREATE EXTENSION IF NOT EXISTS pg_trgm;

        CREATE INDEX IF NOT EXISTS idx_patients_name_trgm
            ON patients USING gin (name gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS idx_patients_pid_trgm
            ON patients USING gin (patient_id gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS idx_exams_accession_trgm
            ON exams USING gin (accession gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS idx_exams_description_trgm
            ON exams USING gin (description gin_trgm_ops);

        -- Partial index for routing queue processor queries
        CREATE INDEX IF NOT EXISTS idx_routing_log_queue
            ON routing_log(status, attempts, queued_at)
            WHERE status IN ('queued', 'failed') AND attempts < 3;

        -- Foreign key indexes (critical for JOIN and CASCADE performance at scale)
        CREATE INDEX IF NOT EXISTS idx_exams_patient_id ON exams(patient_id);
        CREATE INDEX IF NOT EXISTS idx_series_exam_id ON series(exam_id);
        CREATE INDEX IF NOT EXISTS idx_instances_series_id ON instances(series_id);
        CREATE INDEX IF NOT EXISTS idx_routing_log_instance_id ON routing_log(instance_id);
        CREATE INDEX IF NOT EXISTS idx_routing_log_destination_id ON routing_log(destination_id);
        CREATE INDEX IF NOT EXISTS idx_routing_log_rule_id ON routing_log(rule_id);
        CREATE INDEX IF NOT EXISTS idx_remote_routing_log_destination_id ON remote_routing_log(destination_id);
        CREATE INDEX IF NOT EXISTS idx_remote_routing_log_rule_id ON remote_routing_log(rule_id);

        -- Query-pattern indexes (columns used in WHERE/ORDER BY)
        CREATE INDEX IF NOT EXISTS idx_exams_study_date ON exams(study_date DESC);
        CREATE INDEX IF NOT EXISTS idx_exams_modality ON exams(modality);
        CREATE INDEX IF NOT EXISTS idx_instances_received_at ON instances(received_at);
        CREATE INDEX IF NOT EXISTS idx_instances_status ON instances(status);
        CREATE INDEX IF NOT EXISTS idx_routing_log_queued_at ON routing_log(queued_at DESC);
        CREATE INDEX IF NOT EXISTS idx_remote_routing_log_study_uid ON remote_routing_log(study_uid);

        -- Non-destructive migration: add claimed_at column to remote_routing_log
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='remote_routing_log' AND column_name='claimed_at'
          ) THEN
            ALTER TABLE remote_routing_log ADD COLUMN claimed_at TIMESTAMPTZ;
          END IF;
        END $$;

        -- Partial index for pending remote routes (used by polling endpoint)
        CREATE INDEX IF NOT EXISTS idx_remote_routing_log_pending
            ON remote_routing_log(remote_agent_ae) WHERE status IN ('published', 'claimed');
        """;

    public static async Task RunAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            var db = services.GetRequiredService<ArchiveDbContext>();
            await db.Database.ExecuteSqlRawAsync(Ddl);
            logger.LogInformation("Schema initializer: all tables verified/created");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schema initializer failed — database may be unavailable");
            throw; // Fail fast on startup so Aspire shows the error clearly
        }
    }
}
