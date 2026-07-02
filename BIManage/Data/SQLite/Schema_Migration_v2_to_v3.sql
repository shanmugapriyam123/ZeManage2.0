-- BIManage SQLite Schema Migration: v2 to v3
-- Adds missing tables and columns for RBAC, Protection Settings, and Evidence Capture
-- Run this on existing databases to bring them up to date

-- =============================================================================
-- STEP 1: Add missing columns to audit_log table (if they don't exist)
-- =============================================================================

-- Check if columns exist before adding (SQLite doesn't have IF NOT EXISTS for columns)
-- You need to run these one by one and ignore errors if column already exists

-- Add RBAC columns to audit_log
ALTER TABLE audit_log ADD COLUMN user_email TEXT NULL;
ALTER TABLE audit_log ADD COLUMN user_id TEXT NULL;
ALTER TABLE audit_log ADD COLUMN user_role TEXT NULL;
ALTER TABLE audit_log ADD COLUMN was_company_admin INTEGER DEFAULT 0;
ALTER TABLE audit_log ADD COLUMN was_project_admin INTEGER DEFAULT 0;

-- =============================================================================
-- STEP 2: Create protection_settings table
-- =============================================================================

CREATE TABLE IF NOT EXISTS protection_settings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id INTEGER NULL,
    name TEXT NOT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- =============================================================================
-- STEP 3: Create command_settings table
-- =============================================================================

CREATE TABLE IF NOT EXISTS command_settings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    protection_settings_id INTEGER NOT NULL,
    command_code TEXT NOT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    intervention_mode INTEGER NOT NULL DEFAULT 0, -- 0=Monitor, 1=Prevent, 2=Guidance
    custom_message TEXT NULL,
    is_company_level INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (protection_settings_id) REFERENCES protection_settings(id) ON DELETE CASCADE
);

-- =============================================================================
-- STEP 4: Create indices for protection tables
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_protection_settings_project ON protection_settings(project_id);
CREATE INDEX IF NOT EXISTS idx_command_settings_protection ON command_settings(protection_settings_id);
CREATE INDEX IF NOT EXISTS idx_command_settings_code ON command_settings(command_code);

-- =============================================================================
-- STEP 5: Insert default protection settings (if none exist)
-- =============================================================================

INSERT OR IGNORE INTO protection_settings (id, project_id, name, is_enabled)
VALUES (1, NULL, 'Default Protection Settings', 1);

-- =============================================================================
-- STEP 6: Update schema version
-- =============================================================================

-- Update schema version to 3
INSERT OR REPLACE INTO schema_version (version) VALUES (3);

-- =============================================================================
-- VERIFICATION QUERIES (uncomment to run checks)
-- =============================================================================

-- SELECT 'audit_log columns:' as check;
-- PRAGMA table_info(audit_log);

-- SELECT 'protection_settings exists:' as check;
-- SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='protection_settings';

-- SELECT 'command_settings exists:' as check;
-- SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='command_settings';

-- SELECT 'Schema version:' as check;
-- SELECT version FROM schema_version;
