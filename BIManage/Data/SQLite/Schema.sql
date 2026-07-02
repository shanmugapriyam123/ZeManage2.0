-- BIManage SQLite Schema - Protection & Rules Engine
-- Version 1: Production schema — Rules, Audit, Pin Protection, Command Protection, Event Protection

-- schema_version table is created by Schema_Persistence.sql (loaded first)
INSERT OR REPLACE INTO schema_version (version) VALUES (1);

-- =============================================================================
-- RULES ENGINE
-- =============================================================================

-- Rules table: Core rule definitions
CREATE TABLE IF NOT EXISTS rules (
    rule_id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    description TEXT NULL,

    -- Scope & Assignment
    rule_scope INTEGER NOT NULL DEFAULT 1,
    project_id TEXT NULL,
    company_id TEXT NULL,
    model_guid TEXT NULL,

    -- Target Matching
    category_id INTEGER NULL,
    category_code TEXT NULL,
    category_name TEXT NULL,
    type_name TEXT NULL,
    family_name TEXT NULL,
    command_name TEXT NULL,

    -- Protection Behavior
    mode INTEGER NOT NULL DEFAULT 1,
    priority INTEGER NOT NULL DEFAULT 1,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    message TEXT NULL,
    capture_before_screenshot INTEGER NOT NULL DEFAULT 1,
    capture_after_screenshot INTEGER NOT NULL DEFAULT 1,
    require_comment INTEGER NOT NULL DEFAULT 0,
    allow_admin_override INTEGER NOT NULL DEFAULT 1,
    send_email INTEGER NOT NULL DEFAULT 0,

    -- Metadata
    version INTEGER NOT NULL DEFAULT 1,
    created_by TEXT NULL,
    modified_by TEXT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    modified_at TEXT NOT NULL DEFAULT (datetime('now')),

    -- Tamper detection
    row_hmac TEXT
);

-- Rule parameters: Conditions for matching elements
CREATE TABLE IF NOT EXISTS rule_parameters (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id TEXT NOT NULL,
    parameter_name TEXT NOT NULL,
    operator INTEGER NOT NULL DEFAULT 0,
    value TEXT NOT NULL,
    ignore_case INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (rule_id) REFERENCES rules(rule_id) ON DELETE CASCADE
);

-- Rule built-in parameters: Conditions using Revit BuiltInParameter enum (type-safe, language-independent)
CREATE TABLE IF NOT EXISTS rule_builtin_parameters (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id TEXT NOT NULL,
    builtin_parameter_id INTEGER NOT NULL,
    operator INTEGER NOT NULL DEFAULT 0,
    value TEXT NOT NULL,
    ignore_case INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (rule_id) REFERENCES rules(rule_id) ON DELETE CASCADE
);

-- Rule commands: Commands this rule applies to
CREATE TABLE IF NOT EXISTS rule_commands (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id TEXT NOT NULL,
    command_id INTEGER NOT NULL,
    command_name TEXT NULL,
    FOREIGN KEY (rule_id) REFERENCES rules(rule_id) ON DELETE CASCADE
);

-- Rule conflicts: Detected conflicts between rules
CREATE TABLE IF NOT EXISTS rule_conflicts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id_1 TEXT NOT NULL,
    rule_id_2 TEXT NOT NULL,
    conflict_type TEXT NOT NULL,
    description TEXT NOT NULL,
    detected_at TEXT NOT NULL DEFAULT (datetime('now')),
    resolved INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (rule_id_1) REFERENCES rules(rule_id) ON DELETE CASCADE,
    FOREIGN KEY (rule_id_2) REFERENCES rules(rule_id) ON DELETE CASCADE
);

-- Rules indices
CREATE INDEX IF NOT EXISTS idx_rules_enabled ON rules(is_enabled);
CREATE INDEX IF NOT EXISTS idx_rules_scope ON rules(rule_scope);
CREATE INDEX IF NOT EXISTS idx_rules_project ON rules(project_id);
CREATE INDEX IF NOT EXISTS idx_rules_company ON rules(company_id);
CREATE INDEX IF NOT EXISTS idx_rules_model_guid ON rules(model_guid);
CREATE INDEX IF NOT EXISTS idx_rules_category ON rules(category_id);
CREATE INDEX IF NOT EXISTS idx_rules_category_code ON rules(category_code);
CREATE INDEX IF NOT EXISTS idx_rules_priority ON rules(priority DESC);
CREATE INDEX IF NOT EXISTS idx_rule_parameters_rule ON rule_parameters(rule_id);
CREATE INDEX IF NOT EXISTS idx_rule_builtin_parameters_rule ON rule_builtin_parameters(rule_id);
CREATE INDEX IF NOT EXISTS idx_rule_commands_rule ON rule_commands(rule_id);
CREATE INDEX IF NOT EXISTS idx_rule_commands_command ON rule_commands(command_id);
CREATE INDEX IF NOT EXISTS idx_rule_conflicts_unresolved ON rule_conflicts(resolved) WHERE resolved = 0;

-- =============================================================================
-- AUDIT LOG
-- =============================================================================

-- Audit log: Track all protection actions
CREATE TABLE IF NOT EXISTS audit_log (
    audit_log_id TEXT PRIMARY KEY,
    timestamp TEXT NOT NULL,
    user_name TEXT NOT NULL,
    was_company_admin INTEGER DEFAULT 0,
    was_project_admin INTEGER DEFAULT 0,
    model_guid TEXT NULL,

    -- Protection source — stores rule_id, command_settings id, event_protection id, or pin_protection id
    protection_id TEXT NULL,
    command_name TEXT NULL,
    mode TEXT NOT NULL,
    action TEXT NOT NULL,

    -- Element context
    element_ids TEXT NULL,
    element_count INTEGER DEFAULT 0,
    element_category TEXT NULL,
    element_family_type TEXT NULL,
    element_name TEXT NULL,
    reason TEXT NULL,
    user_comment TEXT NULL,

    -- Event source — identifies which protection system logged the event
    -- Values: "CommandProtection", "RuleProtection", "PinProtection", "EventProtection"
    event_source TEXT DEFAULT 'CommandProtection',

    -- Override tracking — "OTP", "AdminPassword", or NULL
    override_method TEXT NULL,

    -- Session link
    session_id TEXT NULL,

    -- Mail dispatch — plugin calls /send-mail endpoint AFTER evidence is uploaded
    sent_mail INTEGER NOT NULL DEFAULT 0,       -- 0 = pending dispatch, 1 = dispatched
    mail_queued_at TEXT NULL,

    -- Sync tracking
    synced INTEGER NOT NULL DEFAULT 0,

    -- Tamper detection
    row_hmac TEXT
);

-- Audit log indices
CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_audit_user ON audit_log(user_name);
CREATE INDEX IF NOT EXISTS idx_audit_mode ON audit_log(mode);
CREATE INDEX IF NOT EXISTS idx_audit_action ON audit_log(action);
CREATE INDEX IF NOT EXISTS idx_audit_model_guid ON audit_log(model_guid);
CREATE INDEX IF NOT EXISTS idx_audit_sent_mail ON audit_log(sent_mail);
CREATE INDEX IF NOT EXISTS idx_audit_protection_id ON audit_log(protection_id);
CREATE INDEX IF NOT EXISTS idx_audit_synced ON audit_log(synced);

-- =============================================================================
-- PASSWORD & OVERRIDE MANAGEMENT
-- =============================================================================

-- Protection passwords: Store encrypted passwords
CREATE TABLE IF NOT EXISTS protection_passwords (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    password_hash TEXT NOT NULL,
    salt TEXT NOT NULL,
    created_date TEXT NOT NULL DEFAULT (datetime('now')),
    updated_date TEXT NOT NULL DEFAULT (datetime('now')),

    -- Tamper detection
    row_hmac TEXT
);

-- Protection overrides: Store structured admin overrides with metadata
CREATE TABLE IF NOT EXISTS protection_overrides (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    override_id TEXT NOT NULL UNIQUE,
    rule_id TEXT NULL,
    command_id TEXT NULL,
    document_id TEXT NULL,
    scope INTEGER NOT NULL DEFAULT 0,
    approver_email TEXT NOT NULL,
    approver_name TEXT NOT NULL,
    reason TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    expires_at TEXT NULL,
    revoked_at TEXT NULL,
    revoked_by TEXT NULL
);

-- Override indices
CREATE INDEX IF NOT EXISTS idx_override_active ON protection_overrides(is_active);
CREATE INDEX IF NOT EXISTS idx_override_rule ON protection_overrides(rule_id);
CREATE INDEX IF NOT EXISTS idx_override_command ON protection_overrides(command_id);
CREATE INDEX IF NOT EXISTS idx_override_expires ON protection_overrides(expires_at);

-- =============================================================================
-- PIN PROTECTION
-- =============================================================================

-- Pin Protection: Central registry of all protected pins
CREATE TABLE IF NOT EXISTS pin_protection (
    id TEXT PRIMARY KEY NOT NULL,
    session_id TEXT NULL,
    model_guid TEXT NOT NULL,
    element_guid TEXT NOT NULL,
    element_id INTEGER NOT NULL,
    element_name TEXT NULL,
    element_category TEXT NULL,
    project_name TEXT NULL,

    -- Protection configuration
    protection_mode INTEGER NOT NULL,           -- 0=Monitor, 1=Guide, 2=Prevent
    protected_by TEXT NOT NULL,
    protected_at TEXT NOT NULL,
    admin_comment TEXT NULL,
    require_comment_for_unpin INTEGER NOT NULL DEFAULT 0,
    instantly_notify_on_unpin INTEGER NOT NULL DEFAULT 0,

    -- Status
    is_active INTEGER NOT NULL DEFAULT 1,
    deactivated_at TEXT NULL,
    deactivated_by TEXT NULL,
    deactivation_reason TEXT NULL,

    -- Sync
    last_synced_at TEXT NOT NULL DEFAULT (datetime('now')),
    sync_source TEXT NOT NULL DEFAULT 'ExtensibleStorage',

    -- Tamper detection
    row_hmac TEXT,

    UNIQUE(model_guid, element_guid),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE SET NULL
);

-- Pin protection indices
CREATE INDEX IF NOT EXISTS idx_pin_protection_model ON pin_protection(model_guid);
CREATE INDEX IF NOT EXISTS idx_pin_protection_session ON pin_protection(session_id);
CREATE INDEX IF NOT EXISTS idx_pin_protection_element ON pin_protection(element_guid);
CREATE INDEX IF NOT EXISTS idx_pin_protection_active ON pin_protection(is_active) WHERE is_active = 1;
CREATE INDEX IF NOT EXISTS idx_pin_protection_mode ON pin_protection(protection_mode);
CREATE INDEX IF NOT EXISTS idx_pin_protection_protected_by ON pin_protection(protected_by);
CREATE INDEX IF NOT EXISTS idx_pin_protection_protected_at ON pin_protection(protected_at DESC);
CREATE INDEX IF NOT EXISTS idx_pin_protection_model_active ON pin_protection(model_guid, is_active);

-- =============================================================================
-- PROTECTION SETTINGS & COMMAND PROTECTION
-- =============================================================================

-- Protection settings: Global protection configuration
CREATE TABLE IF NOT EXISTS protection_settings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guid TEXT NOT NULL DEFAULT '',
    project_id INTEGER NULL,
    name TEXT NOT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Insert default protection settings
INSERT OR IGNORE INTO protection_settings (id, guid, project_id, name, is_enabled)
VALUES (1, '00000000-0000-0000-0000-000000000001', NULL, 'Default Protection Settings', 1);

-- Command settings: Command-specific protection rules
CREATE TABLE IF NOT EXISTS command_settings (
    id TEXT PRIMARY KEY NOT NULL,
    protection_settings_id TEXT NOT NULL,

    -- Scope & Assignment
    project_id TEXT NULL,
    company_id TEXT NULL,
    model_guid TEXT NULL,
    scope INTEGER NOT NULL DEFAULT 0,           -- 0=Company, 1=Project, 2=Model
    is_company_level INTEGER NOT NULL DEFAULT 0,

    -- Command
    command_code TEXT NOT NULL,

    -- Protection Behavior
    is_enabled INTEGER NOT NULL DEFAULT 1,
    intervention_mode INTEGER NOT NULL DEFAULT 0,
    custom_message TEXT NULL,
    custom_message_image_path TEXT NULL,
    capture_before_screenshot INTEGER NOT NULL DEFAULT 0,
    capture_after_screenshot INTEGER NOT NULL DEFAULT 0,
    require_comment INTEGER NOT NULL DEFAULT 0,
    allow_admin_override INTEGER NOT NULL DEFAULT 1,
    send_email INTEGER NOT NULL DEFAULT 0,

    -- Metadata
    created_by TEXT NULL,
    modified_by TEXT NULL,
    project_assigned TEXT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now')),

    -- Tamper detection
    row_hmac TEXT,

    FOREIGN KEY (protection_settings_id) REFERENCES protection_settings(guid) ON DELETE CASCADE
);

-- Command settings indices
CREATE INDEX IF NOT EXISTS idx_protection_settings_project ON protection_settings(project_id);
CREATE INDEX IF NOT EXISTS idx_command_settings_protection ON command_settings(protection_settings_id);
CREATE INDEX IF NOT EXISTS idx_command_settings_code ON command_settings(command_code);

-- =============================================================================
-- EVENT PROTECTION
-- =============================================================================

-- Event protection settings: event-based protections
CREATE TABLE IF NOT EXISTS event_protection_settings (
    id TEXT PRIMARY KEY NOT NULL,
    protection_settings_id TEXT NOT NULL,

    -- Event identification
    event_type INTEGER NOT NULL,
    dummy_command_id TEXT NOT NULL,
    protection_name TEXT NOT NULL,

    -- Scope & Assignment
    project_id TEXT NULL,
    company_id TEXT NULL,
    model_guid TEXT NULL,
    is_company_level INTEGER DEFAULT 1,

    -- Protection Behavior
    is_enabled INTEGER DEFAULT 0,
    intervention_mode INTEGER DEFAULT 0,
    custom_message TEXT NULL,
    custom_message_image_path TEXT NULL,
    configuration_json TEXT NULL,
    capture_before_screenshot INTEGER DEFAULT 0,
    capture_after_screenshot INTEGER DEFAULT 0,
    require_comment INTEGER DEFAULT 0,
    allow_admin_override INTEGER DEFAULT 1,
    send_email INTEGER DEFAULT 0,

    -- Rule links
    rule_ids TEXT NULL,

    -- Metadata
    created_by TEXT NULL,
    modified_by TEXT NULL,
    updated_at TEXT NULL,

    -- Tamper detection
    row_hmac TEXT,

    FOREIGN KEY (protection_settings_id) REFERENCES protection_settings(guid)
);

-- Event protection indices
CREATE INDEX IF NOT EXISTS idx_event_protection_event_type ON event_protection_settings(event_type);
CREATE INDEX IF NOT EXISTS idx_event_protection_dummy_cmd ON event_protection_settings(dummy_command_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_event_protection_unique ON event_protection_settings(protection_settings_id, event_type, dummy_command_id);

-- =============================================================================
-- SYNC CONTROL
-- =============================================================================

-- Sync queue: Sync traffic control for workshared documents
CREATE TABLE IF NOT EXISTS sync_queue (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    queue_id TEXT NOT NULL UNIQUE,
    model_guid TEXT NOT NULL,
    model_name TEXT NOT NULL,
    session_id TEXT NOT NULL,
    username TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'waiting',
    requested_at TEXT NOT NULL,
    started_at TEXT NULL,
    completed_at TEXT NULL,
    error_message TEXT NULL,
    created_at TEXT NOT NULL
);

-- =============================================================================
-- UNMONITORED USER DETECTIONS
-- =============================================================================

-- Track users working on models without BIManage running
CREATE TABLE IF NOT EXISTS unmonitored_user_detections (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_guid TEXT NOT NULL,
    revit_username TEXT NOT NULL,
    detection_source TEXT NOT NULL,
    detected_at TEXT NOT NULL,
    synced INTEGER DEFAULT 0,
    UNIQUE(model_guid, revit_username, detection_source)
);

CREATE INDEX IF NOT EXISTS idx_unmonitored_model ON unmonitored_user_detections(model_guid);
CREATE INDEX IF NOT EXISTS idx_unmonitored_synced ON unmonitored_user_detections(synced) WHERE synced = 0;

-- =============================================================================
-- SBOM (Software Bill of Materials)
-- =============================================================================

-- SBOM components: Track all third-party dependencies for license/security auditing
CREATE TABLE IF NOT EXISTS sbom_components (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    component_type TEXT NOT NULL,
    component_name TEXT NOT NULL,
    component_version TEXT NOT NULL,
    purl TEXT NULL,
    license_id TEXT NULL,
    license_name TEXT NULL,
    publisher TEXT NULL,
    description TEXT NULL,
    scope TEXT NULL,
    hash_alg TEXT NULL,
    hash_value TEXT NULL,
    sbom_format TEXT NOT NULL DEFAULT 'CycloneDX',
    sbom_version TEXT NULL,
    source_project TEXT NULL,
    target_framework TEXT NULL,
    generated_at TEXT NOT NULL,
    imported_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(component_name, component_version, source_project, target_framework)
);

-- SBOM metadata: Single-row tracking of last SBOM import
CREATE TABLE IF NOT EXISTS sbom_metadata (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    file_hash_sha256 TEXT NOT NULL,
    component_count INTEGER NOT NULL,
    last_updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_sbom_name ON sbom_components(component_name);
CREATE INDEX IF NOT EXISTS idx_sbom_license ON sbom_components(license_id);
CREATE INDEX IF NOT EXISTS idx_sbom_type ON sbom_components(component_type);

-- Sync queue indices
CREATE INDEX IF NOT EXISTS idx_sync_queue_model ON sync_queue(model_guid);
CREATE INDEX IF NOT EXISTS idx_sync_queue_session ON sync_queue(session_id);
CREATE INDEX IF NOT EXISTS idx_sync_queue_status ON sync_queue(status);
CREATE INDEX IF NOT EXISTS idx_sync_queue_model_status ON sync_queue(model_guid, status);
CREATE INDEX IF NOT EXISTS idx_sync_queue_requested ON sync_queue(requested_at DESC);

-- Background sync settings: Per-user sync configuration (single row)
CREATE TABLE IF NOT EXISTS background_sync_settings (
    id INTEGER PRIMARY KEY,

    -- Core settings
    is_enabled INTEGER NOT NULL DEFAULT 1,
    sync_interval_minutes INTEGER NOT NULL DEFAULT 30,
    relinquish_interval_minutes INTEGER NOT NULL DEFAULT 60,
    enable_idle_sync INTEGER NOT NULL DEFAULT 1,
    idle_timeout_minutes INTEGER NOT NULL DEFAULT 15,
    enable_relinquish INTEGER NOT NULL DEFAULT 1,
    sync_on_save INTEGER NOT NULL DEFAULT 0,

    -- Extended settings
    sync_all_the_time INTEGER NOT NULL DEFAULT 0,
    sync_even_if_no_changes INTEGER NOT NULL DEFAULT 0,
    enable_schedule INTEGER NOT NULL DEFAULT 0,
    schedule_start_time TEXT NOT NULL DEFAULT '21:00',
    schedule_end_time TEXT NOT NULL DEFAULT '09:00',
    compact_model_once_a_day INTEGER NOT NULL DEFAULT 0,
    compact_at_night_only INTEGER NOT NULL DEFAULT 0,
    exit_revit_on_idle INTEGER NOT NULL DEFAULT 0,
    exit_revit_after_minutes INTEGER NOT NULL DEFAULT 1440,

    -- Open-views behaviour
    -- 0 = Keep them open, 1 = Close all views, 2 = Close all views and reopen after sync
    open_views_on_sync_mode INTEGER NOT NULL DEFAULT 0,
    -- Block sync when more than this many views are open (min 2, default 10)
    prevent_sync_when_views_opened_over INTEGER NOT NULL DEFAULT 10,

    -- Timestamps
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
);
