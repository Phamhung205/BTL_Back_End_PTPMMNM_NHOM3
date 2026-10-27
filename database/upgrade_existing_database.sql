-- Nang cap database da co 15 bang nghiep vu theo schema Backend hien tai.
-- Danh cho MariaDB/XAMPP. Chon dung database; doi USE neu ten khac.
-- DDL co the implicit COMMIT. Nen export database truoc khi import.
USE translation_manager;
SET NAMES utf8mb4 COLLATE utf8mb4_unicode_ci;

-- 1. Ban dich, snapshot phat hanh (migration 002).
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

-- 2. Ten ngon ngu theo du an, khoa JSON phan biet hoa/thuong/dau (003).
ALTER TABLE project_locales
  ADD COLUMN IF NOT EXISTS display_name VARCHAR(80) NULL AFTER is_source;
ALTER TABLE source_strings
  MODIFY key_path VARCHAR(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;
ALTER TABLE source_string_versions
  MODIFY key_path VARCHAR(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;

-- 3. Quyen Admin, khoa tai khoan va phien ban cap nhat tai khoan (004).
ALTER TABLE users
  ADD COLUMN IF NOT EXISTS system_role ENUM('user','admin') NOT NULL DEFAULT 'user',
  ADD COLUMN IF NOT EXISTS is_active TINYINT(1) NOT NULL DEFAULT 1,
  ADD COLUMN IF NOT EXISTS security_version INT NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS system_admin_guard (
  id TINYINT UNSIGNED PRIMARY KEY
) ENGINE=InnoDB;
INSERT IGNORE INTO system_admin_guard(id) VALUES (1);

CREATE TABLE IF NOT EXISTS admin_audit (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  actor_id BIGINT UNSIGNED NULL,
  action VARCHAR(60) NOT NULL,
  target_type VARCHAR(30) NOT NULL,
  target_id BIGINT UNSIGNED NULL,
  summary TEXT NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_admin_audit_date(created_at,id),
  CONSTRAINT fk_admin_audit_actor FOREIGN KEY(actor_id)
    REFERENCES users(id) ON DELETE SET NULL
) ENGINE=InnoDB;

-- 4. Danh muc ngon ngu. Giữ ten da co; chua tu gan vao du an.
INSERT IGNORE INTO locales(code,name) VALUES
('vi','Tiếng Việt'),
('en','English'),
('zh','中文'),
('ja','Tiếng Nhật (日本語)'),
('ko','Tiếng Hàn (한국어)'),
('fr','Tiếng Pháp (Français)'),
('de','Tiếng Đức (Deutsch)'),
('es','Tiếng Tây Ban Nha (Español)'),
('pt','Tiếng Bồ Đào Nha (Português)'),
('ru','Tiếng Nga (Русский)'),
('th','Tiếng Thái (ไทย)');

-- 5. Xem ket qua. Hai bang Admin phai xuat hien trong danh sach.
SHOW TABLES;
SELECT column_name,column_type,is_nullable
FROM information_schema.columns
WHERE table_schema=DATABASE() AND table_name='users'
  AND column_name IN ('system_role','is_active','security_version')
ORDER BY ordinal_position;

-- Buoc tiep theo: xem setup_existing_admin_and_languages.sql.
