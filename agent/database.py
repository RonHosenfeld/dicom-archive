"""
database.py — Postgres index for stored DICOM instances
Schema: patients → exams (studies) → series → instances
"""

import os
import logging
import psycopg2
import psycopg2.extras
from contextlib import contextmanager
from typing import Optional

logger = logging.getLogger(__name__)

DDL = """
CREATE TABLE IF NOT EXISTS patients (
    id          SERIAL PRIMARY KEY,
    patient_id  TEXT NOT NULL,          -- DICOM (0010,0020)
    name        TEXT,                   -- DICOM (0010,0010)
    birth_date  DATE,                   -- DICOM (0010,0030)
    sex         CHAR(1),                -- DICOM (0010,0040)
    created_at  TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE (patient_id)
);

CREATE TABLE IF NOT EXISTS exams (
    id              SERIAL PRIMARY KEY,
    patient_id      INTEGER REFERENCES patients(id),
    study_uid       TEXT NOT NULL UNIQUE,   -- (0020,000D)
    study_date      DATE,                   -- (0008,0020)
    study_time      TEXT,                   -- (0008,0030)
    accession       TEXT,                   -- (0008,0050)
    description     TEXT,                   -- (0008,1030)
    modality        TEXT,                   -- (0008,0060)
    referring_physician TEXT,               -- (0008,0090)
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS series (
    id              SERIAL PRIMARY KEY,
    exam_id         INTEGER REFERENCES exams(id),
    series_uid      TEXT NOT NULL UNIQUE,   -- (0020,000E)
    series_number   INTEGER,                -- (0020,0011)
    series_date     DATE,                   -- (0008,0021)
    body_part       TEXT,                   -- (0018,0015)
    description     TEXT,                   -- (0008,103E)
    laterality      TEXT,                   -- (0020,0060) L/R for mammo
    view_position   TEXT,                   -- (0018,5101) CC/MLO for mammo
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS instances (
    id              SERIAL PRIMARY KEY,
    series_id       INTEGER REFERENCES series(id),
    instance_uid    TEXT NOT NULL UNIQUE,   -- (0008,0018)
    instance_number INTEGER,                -- (0020,0013)
    blob_key        TEXT NOT NULL,          -- path/key in object storage
    blob_uri        TEXT,                   -- full URI returned by storage backend
    size_bytes      BIGINT,
    sha256          TEXT,
    transfer_syntax TEXT,                   -- (0002,0010)
    rows            INTEGER,                -- (0028,0010)
    columns         INTEGER,               -- (0028,0011)
    received_at     TIMESTAMPTZ DEFAULT NOW(),
    sending_ae      TEXT,                   -- AE title of the modality/sender
    receiving_ae    TEXT                    -- AE title of the agent that received it
);

-- Non-destructive migration: add receiving_ae if upgrading from an earlier version
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_name='instances' AND column_name='receiving_ae'
  ) THEN
    ALTER TABLE instances ADD COLUMN receiving_ae TEXT;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_instances_series  ON instances(series_id);
CREATE INDEX IF NOT EXISTS idx_series_exam        ON series(exam_id);
CREATE INDEX IF NOT EXISTS idx_exams_patient      ON exams(patient_id);
CREATE INDEX IF NOT EXISTS idx_exams_study_date   ON exams(study_date);
CREATE INDEX IF NOT EXISTS idx_instances_uid      ON instances(instance_uid);
"""


class Database:
    def __init__(self, dsn: str):
        self.dsn = dsn
        self._conn = None

    def connect(self):
        self._conn = psycopg2.connect(self.dsn)
        self._conn.autocommit = False
        logger.info("Connected to Postgres")
        self._ensure_schema()

    def _ensure_schema(self):
        with self._conn.cursor() as cur:
            cur.execute(DDL)
        self._conn.commit()
        logger.info("Schema verified")

    @contextmanager
    def cursor(self):
        cur = self._conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor)
        try:
            yield cur
            self._conn.commit()
        except Exception:
            self._conn.rollback()
            raise
        finally:
            cur.close()

    def upsert_patient(self, ds) -> int:
        patient_id = getattr(ds, "PatientID", "UNKNOWN")
        name       = str(getattr(ds, "PatientName", "")) or None
        birth_date = _parse_date(getattr(ds, "PatientBirthDate", None))
        sex        = str(getattr(ds, "PatientSex", ""))[:1] or None

        with self.cursor() as cur:
            cur.execute("""
                INSERT INTO patients (patient_id, name, birth_date, sex)
                VALUES (%s, %s, %s, %s)
                ON CONFLICT (patient_id) DO UPDATE
                    SET name = EXCLUDED.name,
                        birth_date = COALESCE(EXCLUDED.birth_date, patients.birth_date),
                        sex  = COALESCE(EXCLUDED.sex,  patients.sex)
                RETURNING id
            """, (patient_id, name, birth_date, sex))
            return cur.fetchone()["id"]

    def upsert_exam(self, ds, patient_db_id: int) -> int:
        study_uid = str(ds.StudyInstanceUID)
        with self.cursor() as cur:
            cur.execute("""
                INSERT INTO exams (patient_id, study_uid, study_date, study_time,
                                   accession, description, modality, referring_physician)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
                ON CONFLICT (study_uid) DO UPDATE
                    SET study_date = COALESCE(EXCLUDED.study_date, exams.study_date)
                RETURNING id
            """, (
                patient_db_id,
                study_uid,
                _parse_date(getattr(ds, "StudyDate", None)),
                str(getattr(ds, "StudyTime", "")) or None,
                str(getattr(ds, "AccessionNumber", "")) or None,
                str(getattr(ds, "StudyDescription", "")) or None,
                str(getattr(ds, "Modality", "")) or None,
                str(getattr(ds, "ReferringPhysicianName", "")) or None,
            ))
            return cur.fetchone()["id"]

    def upsert_series(self, ds, exam_db_id: int) -> int:
        series_uid = str(ds.SeriesInstanceUID)
        with self.cursor() as cur:
            cur.execute("""
                INSERT INTO series (exam_id, series_uid, series_number, series_date,
                                    body_part, description, laterality, view_position)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
                ON CONFLICT (series_uid) DO NOTHING
                RETURNING id
            """, (
                exam_db_id,
                series_uid,
                _int(getattr(ds, "SeriesNumber", None)),
                _parse_date(getattr(ds, "SeriesDate", None)),
                str(getattr(ds, "BodyPartExamined", "")) or None,
                str(getattr(ds, "SeriesDescription", "")) or None,
                str(getattr(ds, "Laterality", "")) or None,         # mammo L/R
                str(getattr(ds, "ViewPosition", "")) or None,       # mammo CC/MLO
            ))
            row = cur.fetchone()
            if row:
                return row["id"]
            # Already existed — fetch id
            cur.execute("SELECT id FROM series WHERE series_uid = %s", (series_uid,))
            return cur.fetchone()["id"]

    def insert_instance(self, ds, series_db_id: int,
                        blob_key: str, blob_uri: str,
                        size_bytes: int, sha256: str,
                        sending_ae: str,
                        receiving_ae: str = None) -> int:
        instance_uid = str(ds.SOPInstanceUID)
        transfer_syntax = str(getattr(ds.file_meta, "TransferSyntaxUID", "")) if hasattr(ds, "file_meta") else None

        with self.cursor() as cur:
            cur.execute("""
                INSERT INTO instances (series_id, instance_uid, instance_number,
                                       blob_key, blob_uri, size_bytes, sha256,
                                       transfer_syntax, rows, columns,
                                       sending_ae, receiving_ae)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
                ON CONFLICT (instance_uid) DO NOTHING
                RETURNING id
            """, (
                series_db_id,
                instance_uid,
                _int(getattr(ds, "InstanceNumber", None)),
                blob_key,
                blob_uri,
                size_bytes,
                sha256,
                transfer_syntax,
                _int(getattr(ds, "Rows", None)),
                _int(getattr(ds, "Columns", None)),
                sending_ae,
                receiving_ae,
            ))
            row = cur.fetchone()
            return row["id"] if row else None  # None = duplicate, already stored

    def close(self):
        if self._conn:
            self._conn.close()


# ── Helpers ───────────────────────────────────────────────────────────────────

def _parse_date(val):
    if not val:
        return None
    s = str(val).strip()
    if len(s) == 8:
        try:
            from datetime import date
            return date(int(s[:4]), int(s[4:6]), int(s[6:8]))
        except ValueError:
            pass
    return None

def _int(val):
    if val is None:
        return None
    try:
        return int(val)
    except (ValueError, TypeError):
        return None


def _parse_aspire_connection_string(cs: str) -> str:
    """Convert ADO.NET connection string (Aspire format) to psycopg2 URL.

    Aspire injects:  Host=postgres;Database=dicom-archive;Username=postgres;Password=secret
    psycopg2 wants: postgresql://postgres:secret@postgres:5432/dicom-archive
    """
    parts = {}
    for kv in cs.split(";"):
        kv = kv.strip()
        if "=" in kv:
            k, v = kv.split("=", 1)
            parts[k.strip().lower()] = v.strip()

    host     = parts.get("host", "localhost")
    port     = parts.get("port", "5432")
    database = parts.get("database", "")
    username = parts.get("username", parts.get("user id", "postgres"))
    password = parts.get("password", "")

    from urllib.parse import quote_plus
    return f"postgresql://{quote_plus(username)}:{quote_plus(password)}@{host}:{port}/{database}"


def get_database() -> Optional["Database"]:
    # 1. Standard Docker Compose / manual format
    dsn = os.getenv("DATABASE_URL", "")

    # 2. Aspire-injected format: ConnectionStrings__dicom-archive (ADO.NET style)
    if not dsn:
        aspire_cs = os.getenv("ConnectionStrings__dicom-archive", "")
        if aspire_cs:
            logger.info("Using Aspire-injected connection string (ConnectionStrings__dicom-archive)")
            dsn = _parse_aspire_connection_string(aspire_cs)

    if not dsn:
        logger.info("No DATABASE_URL set — running without Postgres index")
        return None

    db = Database(dsn)
    db.connect()
    return db
