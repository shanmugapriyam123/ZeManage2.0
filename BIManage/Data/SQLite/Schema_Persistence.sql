-- BIManage SQLite Schema - Local Persistence & Offline Cache
-- Version 1: Production schema — all tables at final structure

-- Create schema version table if it doesn't exist
CREATE TABLE IF NOT EXISTS schema_version (
    version INTEGER PRIMARY KEY NOT NULL
);

-- Update schema version
INSERT OR REPLACE INTO schema_version (version) VALUES (1);

-- =============================================================================
-- SESSION MANAGEMENT
-- =============================================================================

-- Sessions table: Track Revit sessions with document context
CREATE TABLE IF NOT EXISTS sessions (
    session_id TEXT PRIMARY KEY NOT NULL,

    -- Machine & Process
    machine_id TEXT NULL,
    process_id INTEGER NULL,

    -- User
    username TEXT NOT NULL,
    revit_username TEXT NULL,
    user_email TEXT NULL,
    computer_name TEXT NOT NULL,

    -- Revit Environment
    revit_version TEXT NOT NULL,
    revit_build TEXT NULL,
    desktop_connector_version TEXT NULL,
    bimanage_version TEXT NULL,
    autodesk_addins INTEGER NULL,
    external_addins INTEGER NULL,
    external_addin_names TEXT NULL,
    loaded_plugin_count INTEGER NULL,          -- DEPRECATED: Use autodesk_addins + external_addins
    journal_file_name TEXT NULL,

    -- Lifecycle Timing
    started_at TEXT NOT NULL,
    opened_at TEXT NULL,
    opening_duration_seconds REAL NULL,
    ended_at TEXT NULL,
    closed_at TEXT NULL,

    -- Status & Counters
    status TEXT NOT NULL DEFAULT 'Active',      -- Active, Inactive, Closed, Crashed
    is_active INTEGER NOT NULL DEFAULT 1,       -- DEPRECATED: Use status field
    crash_detected INTEGER NOT NULL DEFAULT 0,
    total_commands INTEGER NOT NULL DEFAULT 0,
    total_events INTEGER NOT NULL DEFAULT 0,
    last_heartbeat TEXT NOT NULL
);

-- Document sessions: Track documents opened during session
CREATE TABLE IF NOT EXISTS document_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    document_id TEXT NOT NULL,

    -- Central model tracking (workshared documents)
    model_guid TEXT NULL,
    central_model_path TEXT NULL,
    central_model_name TEXT NULL,

    -- Current document path (normalized)
    model_location TEXT NULL,

    -- Local copy tracking
    is_local INTEGER NOT NULL DEFAULT 0,

    -- Document info
    document_title TEXT NOT NULL,
    project_name TEXT NULL,
    cloud_project_name TEXT NULL,

    -- Lifecycle timing
    opened_at TEXT NOT NULL,
    opening_started_at TEXT NULL,
    opening_duration_seconds REAL NULL,
    interactive_ready_seconds REAL NULL,
    opened_worksets_count INTEGER NULL,
    -- Post-open idle waits: accumulated time the user spent on Worksets
    -- and Manage Links dialogs after the document was already open.
    total_workset_open_seconds REAL NULL DEFAULT 0,
    total_link_load_seconds REAL NULL DEFAULT 0,
    closed_at TEXT NULL,

    -- Status & Counters
    is_active INTEGER NOT NULL DEFAULT 1,
    is_crashed INTEGER NOT NULL DEFAULT 0,
    total_modifications INTEGER NOT NULL DEFAULT 0,
    last_saved_at TEXT NULL,

    -- Metadata
    created_by TEXT NULL,
    modified_by TEXT NULL,

    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
    FOREIGN KEY (model_guid) REFERENCES registered_models(model_guid) ON DELETE SET NULL
);

-- Model Registration: Master authorization table for data collection and protection
CREATE TABLE IF NOT EXISTS registered_models (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_guid TEXT NOT NULL UNIQUE,
    model_name TEXT NOT NULL,
    central_model_path TEXT NULL,

    -- Project identification
    project_name TEXT NULL,
    cloud_project_id TEXT NULL,
    zemanage_project_id TEXT NULL,

    -- Model characteristics
    model_type TEXT NULL,                        -- "workshared", "local", "family", "cloudmodel"
    is_local_copy INTEGER NOT NULL DEFAULT 0,
    is_workshared INTEGER NOT NULL DEFAULT 0,
    is_family INTEGER NOT NULL DEFAULT 0,
    is_cloudmodel INTEGER NOT NULL DEFAULT 0,

    -- Status & Activation
    is_active INTEGER NOT NULL DEFAULT 1,
    registered_by TEXT NOT NULL,
    registered_at TEXT NOT NULL DEFAULT (datetime('now')),
    deactivated_at TEXT NULL,
    deactivated_by TEXT NULL,
    deactivation_reason TEXT NULL,

    -- Activity tracking
    last_opened_at TEXT NULL,
    last_opened_by TEXT NULL,
    notes TEXT NULL,

    -- Timestamps
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Application settings: Global configuration (single row)
CREATE TABLE IF NOT EXISTS app_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    auto_register_models INTEGER NOT NULL DEFAULT 1,
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Insert default settings if not exists
INSERT OR IGNORE INTO app_settings (id) VALUES (1);

-- Model sync tracking: Track all sync operations for workshared documents
CREATE TABLE IF NOT EXISTS model_sync (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sync_guid TEXT NOT NULL UNIQUE,
    session_id TEXT NOT NULL,
    model_guid TEXT NOT NULL,
    model_path TEXT NULL,
    model_name TEXT NOT NULL,
    synced_by TEXT NOT NULL,

    -- Sync lifecycle
    sync_started_at TEXT NOT NULL,
    sync_ended_at TEXT NULL,
    sync_duration_seconds REAL NULL,

    -- Result
    is_local_saved INTEGER NOT NULL DEFAULT 0,
    is_succeeded INTEGER NOT NULL DEFAULT 0,
    is_relinquished INTEGER NOT NULL DEFAULT 0,
    error_message TEXT NULL,

    -- Timestamps
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL,

    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- Session heartbeats: Track session health for crash detection
CREATE TABLE IF NOT EXISTS session_heartbeats (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    active_document_id TEXT NULL,
    memory_usage_percent REAL NULL,
    cpu_usage_percent REAL NULL,
    disk_usage_percent REAL NULL,
    graphics_usage_percent REAL NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    modified_at TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- =============================================================================
-- EVENT LOGGING & TRACKING
-- =============================================================================

-- Event log: Comprehensive Revit event tracking for analytics
CREATE TABLE IF NOT EXISTS event_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    document_id TEXT NULL,
    timestamp TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_category TEXT NOT NULL,               -- Document, View, Element, Application, Sync
    event_name TEXT NOT NULL,
    element_id TEXT NULL,
    element_type TEXT NULL,
    transaction_name TEXT NULL,
    success INTEGER NOT NULL DEFAULT 1,
    error_message TEXT NULL,
    metadata TEXT NULL,                          -- JSON blob for additional data
    duration_ms INTEGER NULL,
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- =============================================================================
-- EVIDENCE CAPTURE & COMPLIANCE
-- =============================================================================

-- Evidence capture: Track screenshots and state snapshots for compliance
CREATE TABLE IF NOT EXISTS evidence_capture (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    evidence_id TEXT NOT NULL UNIQUE,
    session_id TEXT NOT NULL,

    -- Protection context
    protection_type TEXT NULL,                   -- command, pin, event, rule
    capture_type TEXT NOT NULL,                  -- screenshot, element_snapshot, document_state
    capture_stage TEXT NOT NULL,                 -- before, after
    captured_at TEXT NOT NULL,

    -- Rule/Command context
    rule_id TEXT NULL,
    rule_name TEXT NULL,
    command_id TEXT NULL,
    command_name TEXT NULL,
    element_ids TEXT NULL,
    element_count INTEGER NOT NULL DEFAULT 0,

    -- File info
    file_path TEXT NULL,
    file_size_bytes INTEGER NULL,
    file_format TEXT NULL,                       -- png, jpg, json
    file_hash_sha256 TEXT NULL,

    -- Upload tracking
    upload_status TEXT NOT NULL DEFAULT 'pending', -- pending, uploading, completed, failed
    uploaded_at TEXT NULL,
    upload_url TEXT NULL,
    upload_error TEXT NULL,
    retry_count INTEGER NOT NULL DEFAULT 0,
    max_retries INTEGER NOT NULL DEFAULT 3,
    last_retry_at TEXT NULL,

    -- Additional data
    metadata TEXT NULL,                          -- JSON blob

    -- Audit link
    audit_log_id TEXT NULL,

    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- =============================================================================
-- OFFLINE OPERATION QUEUE
-- =============================================================================

-- Offline queue: Store operations performed while offline
CREATE TABLE IF NOT EXISTS offline_queue (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    queue_id TEXT NOT NULL UNIQUE,
    refer_id TEXT NOT NULL,
    operation_type TEXT NOT NULL,                -- audit_log, rule_update, event_log, etc.
    operation_data TEXT NOT NULL,                -- JSON blob
    priority INTEGER NOT NULL DEFAULT 5,         -- 1=highest, 10=lowest
    sync_status TEXT NOT NULL DEFAULT 'pending', -- pending, in_progress, completed, failed
    retry_count INTEGER NOT NULL DEFAULT 0,
    max_retries INTEGER NOT NULL DEFAULT 3,
    last_retry_at TEXT NULL,
    last_error TEXT NULL,
    created_at TEXT NOT NULL,
    synced_at TEXT NULL,
    FOREIGN KEY (refer_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- Sync log: Track synchronization attempts and results
CREATE TABLE IF NOT EXISTS sync_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sync_id TEXT NOT NULL UNIQUE,
    refer_id TEXT NOT NULL,
    sync_direction TEXT NOT NULL,                -- upload, download, bidirectional
    started_at TEXT NOT NULL,
    completed_at TEXT NULL,
    items_queued INTEGER NOT NULL DEFAULT 0,
    items_synced INTEGER NOT NULL DEFAULT 0,
    items_failed INTEGER NOT NULL DEFAULT 0,
    success INTEGER NOT NULL DEFAULT 0,
    error_message TEXT NULL,
    FOREIGN KEY (refer_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- =============================================================================
-- CACHE TABLES
-- =============================================================================

-- Category cache: Store Revit categories from API for offline use
CREATE TABLE IF NOT EXISTS category_cache (
    category_name TEXT PRIMARY KEY NOT NULL,
    category_code_24 TEXT NULL
);

-- Command cache: Store Revit commands from API for offline use
CREATE TABLE IF NOT EXISTS command_cache (
    command_name TEXT PRIMARY KEY NOT NULL,
    member_name_24 TEXT NULL,
    description TEXT NULL,
    can_have_binding INTEGER NULL DEFAULT 0,
    need_binding INTEGER NULL DEFAULT 0,
    can_work_with_selection INTEGER NULL DEFAULT 0
);

-- =============================================================================
-- MODEL FILE METRICS COLLECTION
-- Performance-tiered metric tracking: Sync/Save (fast), Periodic (daily), Manual (expensive)
-- =============================================================================

-- Table 1: model_file_metrics_sync_save
-- Fast metrics captured automatically before sync/save operations
CREATE TABLE IF NOT EXISTS model_file_metrics_sync_save (
    id INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Capture metadata
    capture_id TEXT NOT NULL UNIQUE,
    session_id TEXT NOT NULL,
    document_id TEXT NOT NULL,
    model_guid TEXT NULL,
    model_path TEXT NULL,
    model_name TEXT NOT NULL,
    capture_type TEXT NOT NULL,                  -- 'sync' or 'save'
    sync_guid TEXT NULL,
    captured_at TEXT NOT NULL,
    captured_by TEXT NOT NULL,

    -- Fast metrics (20 columns)
    file_size_bytes INTEGER NULL,
    levels_count INTEGER NULL,
    grids_count INTEGER NULL,
    design_options_count INTEGER NULL,
    linked_dwg_count INTEGER NULL,
    imported_dwg_count INTEGER NULL,
    linked_revit_count INTEGER NULL,
    raster_images_count INTEGER NULL,
    warnings_count INTEGER NULL,
    duplicate_elements_count INTEGER NULL,
    model_groups_count INTEGER NULL,
    detail_groups_count INTEGER NULL,
    total_views_count INTEGER NULL,
    total_families_count INTEGER NULL,
    total_worksets_count INTEGER NULL,
    sheets_count INTEGER NULL,
    non_native_object_styles_count INTEGER NULL,
    view_templates_count INTEGER NULL,
    shared_coord_ns REAL NULL,
    shared_coord_ew REAL NULL,
    shared_coord_elevation REAL NULL,
    shared_coord_unit TEXT NULL,

    -- Timestamps
    created_at TEXT NOT NULL,

    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
    FOREIGN KEY (sync_guid) REFERENCES model_sync(sync_guid) ON DELETE SET NULL
);

-- Table 2: model_file_metrics_periodic
-- Medium-cost metrics captured once per day via timer
CREATE TABLE IF NOT EXISTS model_file_metrics_periodic (
    id INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Capture metadata
    capture_id TEXT NOT NULL UNIQUE,
    session_id TEXT NOT NULL,
    document_id TEXT NOT NULL,
    model_guid TEXT NULL,
    model_path TEXT NULL,
    model_name TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    captured_by TEXT NOT NULL,
    capture_interval_hours INTEGER NULL DEFAULT 24,
    is_manual_trigger INTEGER NOT NULL DEFAULT 0,

    -- Medium metrics (10 columns)
    total_elements_count INTEGER NULL,
    model_elements_count INTEGER NULL,
    annotative_elements_count INTEGER NULL,
    inplace_families_count INTEGER NULL,
    unplaced_rooms_count INTEGER NULL,
    views_not_on_sheets_count INTEGER NULL,
    unenclosed_rooms_count INTEGER NULL,
    walls_not_connected_count INTEGER NULL,
    pipes_not_connected_count INTEGER NULL,
    ducts_not_connected_count INTEGER NULL,

    -- Timestamps
    created_at TEXT NOT NULL,

    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- Table 3: model_file_metrics_manual
-- Expensive metrics captured only when user clicks "Analyze Model"
CREATE TABLE IF NOT EXISTS model_file_metrics_manual (
    id INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Capture metadata
    capture_id TEXT NOT NULL UNIQUE,
    session_id TEXT NOT NULL,
    document_id TEXT NOT NULL,
    model_guid TEXT NULL,
    model_path TEXT NULL,
    model_name TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    captured_by TEXT NOT NULL,
    command_source TEXT NULL DEFAULT 'ribbon',
    capture_reason TEXT NULL,

    -- Expensive metrics (2 columns)
    families_over_5mb_count INTEGER NULL,
    purgeable_elements_count INTEGER NULL,

    -- Timestamps
    created_at TEXT NOT NULL,

    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
);

-- =============================================================================
-- INDICES
-- =============================================================================

-- Session indices
CREATE INDEX IF NOT EXISTS idx_sessions_active ON sessions(is_active);
CREATE INDEX IF NOT EXISTS idx_sessions_started ON sessions(started_at DESC);
CREATE INDEX IF NOT EXISTS idx_sessions_user ON sessions(username);
CREATE INDEX IF NOT EXISTS idx_sessions_machine_process ON sessions(machine_id, process_id, is_active);
CREATE INDEX IF NOT EXISTS idx_document_sessions_active ON document_sessions(is_active);
CREATE INDEX IF NOT EXISTS idx_document_sessions_session ON document_sessions(session_id);
CREATE INDEX IF NOT EXISTS idx_document_sessions_model_guid ON document_sessions(model_guid);
CREATE INDEX IF NOT EXISTS idx_document_sessions_central_path ON document_sessions(central_model_path);
CREATE INDEX IF NOT EXISTS idx_registered_models_active ON registered_models(is_active) WHERE is_active = 1;
CREATE INDEX IF NOT EXISTS idx_registered_models_name ON registered_models(model_name);
CREATE INDEX IF NOT EXISTS idx_registered_models_project ON registered_models(cloud_project_id);
CREATE INDEX IF NOT EXISTS idx_model_sync_session ON model_sync(session_id);
CREATE INDEX IF NOT EXISTS idx_model_sync_model_guid ON model_sync(model_guid);
CREATE INDEX IF NOT EXISTS idx_model_sync_synced_by ON model_sync(synced_by);
CREATE INDEX IF NOT EXISTS idx_model_sync_started_at ON model_sync(sync_started_at DESC);
CREATE INDEX IF NOT EXISTS idx_model_sync_duration ON model_sync(sync_duration_seconds DESC);
CREATE INDEX IF NOT EXISTS idx_model_sync_success ON model_sync(is_succeeded);
CREATE INDEX IF NOT EXISTS idx_model_sync_user_time ON model_sync(synced_by, sync_started_at DESC);
CREATE INDEX IF NOT EXISTS idx_model_sync_model_time ON model_sync(model_guid, sync_started_at DESC);
CREATE INDEX IF NOT EXISTS idx_heartbeats_session ON session_heartbeats(session_id);
CREATE INDEX IF NOT EXISTS idx_heartbeats_timestamp ON session_heartbeats(timestamp DESC);

-- Event log indices
CREATE INDEX IF NOT EXISTS idx_event_log_session ON event_log(session_id);
CREATE INDEX IF NOT EXISTS idx_event_log_timestamp ON event_log(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_event_log_type ON event_log(event_type);
CREATE INDEX IF NOT EXISTS idx_event_log_category ON event_log(event_category);
CREATE INDEX IF NOT EXISTS idx_event_log_document ON event_log(document_id);

-- Evidence capture indices
CREATE INDEX IF NOT EXISTS idx_evidence_session ON evidence_capture(session_id);
CREATE INDEX IF NOT EXISTS idx_evidence_upload_status ON evidence_capture(upload_status);
CREATE INDEX IF NOT EXISTS idx_evidence_captured_at ON evidence_capture(captured_at DESC);
CREATE INDEX IF NOT EXISTS idx_evidence_rule ON evidence_capture(rule_id);
CREATE INDEX IF NOT EXISTS idx_evidence_protection_type ON evidence_capture(protection_type);
CREATE INDEX IF NOT EXISTS idx_evidence_audit_log_id ON evidence_capture(audit_log_id);

-- Offline queue indices
CREATE INDEX IF NOT EXISTS idx_offline_queue_status ON offline_queue(sync_status);
CREATE INDEX IF NOT EXISTS idx_offline_queue_priority ON offline_queue(priority ASC, created_at ASC);
CREATE INDEX IF NOT EXISTS idx_offline_queue_session ON offline_queue(refer_id);
CREATE INDEX IF NOT EXISTS idx_sync_log_refer ON sync_log(refer_id);
CREATE INDEX IF NOT EXISTS idx_sync_log_started ON sync_log(started_at DESC);

-- Cache indices
CREATE INDEX IF NOT EXISTS idx_category_cache_name ON category_cache(category_name);
CREATE INDEX IF NOT EXISTS idx_command_cache_name ON command_cache(command_name);

-- Metrics indices
CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_session ON model_file_metrics_sync_save(session_id);
CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_capture ON model_file_metrics_sync_save(capture_id);
CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_model ON model_file_metrics_sync_save(model_guid);
CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_type ON model_file_metrics_sync_save(capture_type);
CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_time ON model_file_metrics_sync_save(captured_at DESC);
CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_sync_guid ON model_file_metrics_sync_save(sync_guid) WHERE sync_guid IS NOT NULL;

-- =============================================================================
-- ZE IDENTITY
-- =============================================================================

CREATE TABLE IF NOT EXISTS ze_identity (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    machine_id    TEXT    NOT NULL,
    sid           TEXT    NULL,
    username      TEXT    NOT NULL,
    hostname      TEXT    NOT NULL,
    ze_user_id    TEXT    NULL,
    captured_at   TEXT    NULL,
    registered_at TEXT    NULL
);
CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_session ON model_file_metrics_periodic(session_id);
CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_capture ON model_file_metrics_periodic(capture_id);
CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_model ON model_file_metrics_periodic(model_guid);
CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_time ON model_file_metrics_periodic(captured_at DESC);
CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_manual ON model_file_metrics_periodic(is_manual_trigger) WHERE is_manual_trigger = 1;
CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_session ON model_file_metrics_manual(session_id);
CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_capture ON model_file_metrics_manual(capture_id);
CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_model ON model_file_metrics_manual(model_guid);
CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_time ON model_file_metrics_manual(captured_at DESC);
CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_source ON model_file_metrics_manual(command_source);

-- =============================================================================
-- TRIGGERS
-- =============================================================================

-- Auto-update session heartbeat on event log insert
CREATE TRIGGER IF NOT EXISTS trg_update_session_heartbeat
AFTER INSERT ON event_log
BEGIN
    UPDATE sessions
    SET last_heartbeat = NEW.timestamp,
        total_events = total_events + 1
    WHERE session_id = NEW.session_id;
END;

-- Auto-increment document modification count
CREATE TRIGGER IF NOT EXISTS trg_update_document_modifications
AFTER INSERT ON event_log
WHEN NEW.event_category = 'Element' AND NEW.success = 1
BEGIN
    UPDATE document_sessions
    SET total_modifications = total_modifications + 1
    WHERE document_id = NEW.document_id AND is_active = 1;
END;

-- =============================================================================
-- VIEWS
-- =============================================================================

-- Active sessions with document count
CREATE VIEW IF NOT EXISTS v_active_sessions AS
SELECT
    s.session_id,
    s.started_at,
    s.username,
    s.user_email,
    s.computer_name,
    s.total_commands,
    s.total_events,
    s.last_heartbeat,
    COUNT(DISTINCT ds.document_id) as open_documents,
    MAX(ds.document_title) as current_document
FROM sessions s
LEFT JOIN document_sessions ds ON s.session_id = ds.session_id AND ds.is_active = 1
WHERE s.is_active = 1
GROUP BY s.session_id;

-- Offline queue summary
CREATE VIEW IF NOT EXISTS v_offline_queue_summary AS
SELECT
    sync_status,
    COUNT(*) as item_count,
    MIN(created_at) as oldest_item,
    MAX(created_at) as newest_item,
    SUM(CASE WHEN retry_count >= max_retries THEN 1 ELSE 0 END) as permanently_failed
FROM offline_queue
GROUP BY sync_status;

-- Event statistics by category
CREATE VIEW IF NOT EXISTS v_event_statistics AS
SELECT
    event_category,
    COUNT(*) as total_events,
    SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) as successful,
    SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END) as failed,
    AVG(duration_ms) as avg_duration_ms,
    MIN(timestamp) as first_event,
    MAX(timestamp) as last_event
FROM event_log
GROUP BY event_category;

