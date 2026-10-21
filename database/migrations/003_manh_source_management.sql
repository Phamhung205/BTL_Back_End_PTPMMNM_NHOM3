-- Bùi Đình Mạnh: project-local display names and case-sensitive JSON keys.
-- Existing MariaDB/XAMPP database only; a new schema.sql already includes these changes.
-- Re-running this migration preserves all content and snapshots.
USE translation_manager;

ALTER TABLE project_locales ADD COLUMN IF NOT EXISTS display_name VARCHAR(80) NULL AFTER is_source;
ALTER TABLE source_strings MODIFY key_path VARCHAR(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;
ALTER TABLE source_string_versions MODIFY key_path VARCHAR(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;
