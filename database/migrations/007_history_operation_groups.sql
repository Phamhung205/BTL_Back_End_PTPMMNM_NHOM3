-- Groups per-key history rows created by a single bulk operation in project activity.
-- Existing rows remain visible as individual history entries (operation_id IS NULL).
USE translation_manager;

ALTER TABLE translation_history
  ADD COLUMN IF NOT EXISTS operation_id CHAR(32) NULL AFTER action;

CREATE INDEX IF NOT EXISTS idx_th_operation ON translation_history(operation_id);
