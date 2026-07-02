-- BIManage SQLite Schema - Pin Protection Extension
-- Version 2: Central Registry for Protected Pins
-- Provides: Reporting, Analytics, Audit Trail, Cross-Model Visibility

-- Pin Protection table: Central registry of all protected pins
CREATE TABLE IF NOT EXISTS pin_protection (
    id TEXT PRIMARY KEY NOT NULL,

    -- Model & Session Identification
    model_guid TEXT NOT NULL,           -- Model GUID (unique model identifier)
    session_id TEXT NULL,               -- FK to sessions table (Revit session where protection was created)
    project_name TEXT NULL,             -- Human-readable project name

    -- Element Identification
    element_guid TEXT NOT NULL,         -- Persistent element identifier (UniqueId)
    element_id INTEGER NOT NULL,        -- Current ElementId (may change after copy/move)
    element_name TEXT NULL,             -- Element name for display
    element_category TEXT NULL,         -- Category name (Walls, Doors, etc.)
    element_type TEXT NULL,             -- Type name

    -- Protection Configuration
    protection_mode INTEGER NOT NULL,   -- 0=Monitor, 1=Guide, 2=Prevent
    protected_by TEXT NOT NULL,         -- Admin username who protected it
    protected_at TEXT NOT NULL,         -- UTC timestamp when protected
    admin_comment TEXT NULL,            -- Why was this element protected

    -- Additional Settings
    require_comment_for_unpin INTEGER NOT NULL DEFAULT 0,
    instantly_notify_on_unpin INTEGER NOT NULL DEFAULT 0,

    -- Status Tracking
    is_active INTEGER NOT NULL DEFAULT 1,
    deactivated_at TEXT NULL,
    deactivated_by TEXT NULL,
    deactivation_reason TEXT NULL,

    -- Metadata
    last_synced_at TEXT NOT NULL DEFAULT (datetime('now')),
    sync_source TEXT NOT NULL DEFAULT 'ExtensibleStorage',  -- Where this record came from

    -- Constraints
    UNIQUE(model_guid, element_guid),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE SET NULL
);

-- Indices for performance
CREATE INDEX IF NOT EXISTS idx_pin_protection_model
    ON pin_protection(model_guid);

CREATE INDEX IF NOT EXISTS idx_pin_protection_session
    ON pin_protection(session_id);

CREATE INDEX IF NOT EXISTS idx_pin_protection_element
    ON pin_protection(element_guid);

CREATE INDEX IF NOT EXISTS idx_pin_protection_active
    ON pin_protection(is_active) WHERE is_active = 1;

CREATE INDEX IF NOT EXISTS idx_pin_protection_mode
    ON pin_protection(protection_mode);

CREATE INDEX IF NOT EXISTS idx_pin_protection_protected_by
    ON pin_protection(protected_by);

CREATE INDEX IF NOT EXISTS idx_pin_protection_protected_at
    ON pin_protection(protected_at DESC);

CREATE INDEX IF NOT EXISTS idx_pin_protection_model_active
    ON pin_protection(model_guid, is_active);

-- Views for common queries

-- Active Protected Pins: All currently protected elements
CREATE VIEW IF NOT EXISTS v_active_protected_pins AS
SELECT
    pp.id,
    pp.model_guid,
    pp.session_id,
    pp.project_name,
    pp.element_guid,
    pp.element_id,
    pp.element_name,
    pp.element_category,
    pp.element_type,
    pp.protection_mode,
    CASE pp.protection_mode
        WHEN 0 THEN 'Monitor'
        WHEN 1 THEN 'Guide'
        WHEN 2 THEN 'Prevent'
        ELSE 'Unknown'
    END as protection_mode_name,
    pp.protected_by,
    pp.protected_at,
    pp.admin_comment,
    pp.require_comment_for_unpin,
    pp.instantly_notify_on_unpin,
    pp.last_synced_at
FROM pin_protection pp
WHERE pp.is_active = 1
ORDER BY pp.protected_at DESC;

-- Protection Summary by Model
CREATE VIEW IF NOT EXISTS v_protection_summary_by_project AS
SELECT
    model_guid,
    project_name,
    COUNT(*) as total_protected,
    SUM(CASE WHEN protection_mode = 0 THEN 1 ELSE 0 END) as monitor_count,
    SUM(CASE WHEN protection_mode = 1 THEN 1 ELSE 0 END) as guide_count,
    SUM(CASE WHEN protection_mode = 2 THEN 1 ELSE 0 END) as prevent_count,
    MAX(protected_at) as last_protection_date
FROM pin_protection
WHERE is_active = 1
GROUP BY model_guid, project_name
ORDER BY total_protected DESC;

-- Protection Summary by User
CREATE VIEW IF NOT EXISTS v_protection_summary_by_user AS
SELECT
    protected_by,
    COUNT(*) as total_protected,
    SUM(CASE WHEN is_active = 1 THEN 1 ELSE 0 END) as active_count,
    SUM(CASE WHEN is_active = 0 THEN 1 ELSE 0 END) as deactivated_count,
    MIN(protected_at) as first_protection,
    MAX(protected_at) as last_protection
FROM pin_protection
GROUP BY protected_by
ORDER BY total_protected DESC;

-- Comments for documentation
PRAGMA user_version = 2;
