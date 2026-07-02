-- BIManage SQLite Schema Migration: v3 to v4
-- Adds ze_identity table for MachineId + SID + ZeUserId storage

CREATE TABLE IF NOT EXISTS ze_identity (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    machine_id    TEXT    NOT NULL,
    sid           TEXT    NULL,
    username      TEXT    NOT NULL,
    hostname      TEXT    NOT NULL,
    ze_user_id    TEXT    NULL,
    registered_at TEXT    NULL
);

INSERT OR REPLACE INTO schema_version (version) VALUES (4);
