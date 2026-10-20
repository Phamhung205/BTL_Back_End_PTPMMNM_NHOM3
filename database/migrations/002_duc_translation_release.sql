-- Module Translation / Release (Phạm Thế Đức)
-- CHỈ chạy file này nếu CSDL translation_manager đã được tạo từ schema.sql bản cũ.
-- Nếu tạo CSDL mới bằng schema.sql hiện tại thì KHÔNG cần chạy.
-- Cú pháp "IF NOT EXISTS" trong ALTER TABLE dành cho MariaDB (XAMPP).
USE translation_manager;

-- value NULL = đã phân công nhưng chưa dịch
ALTER TABLE translations MODIFY value TEXT NULL;

ALTER TABLE releases
  ADD COLUMN IF NOT EXISTS source_version_id BIGINT UNSIGNED NULL AFTER version_name,
  ADD COLUMN IF NOT EXISTS note TEXT NULL AFTER source_version_id,
  ADD COLUMN IF NOT EXISTS key_count INT NOT NULL DEFAULT 0 AFTER note;

ALTER TABLE releases
  ADD CONSTRAINT fk_rel_source_version FOREIGN KEY IF NOT EXISTS (source_version_id)
  REFERENCES source_versions(id) ON DELETE SET NULL;

ALTER TABLE release_files
  ADD COLUMN IF NOT EXISTS is_source TINYINT(1) NOT NULL DEFAULT 0 AFTER locale_code,
  ADD COLUMN IF NOT EXISTS key_count INT NOT NULL DEFAULT 0 AFTER sha256;
