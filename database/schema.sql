CREATE DATABASE IF NOT EXISTS translation_manager
  CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE translation_manager;

CREATE TABLE IF NOT EXISTS users (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  email VARCHAR(190) NOT NULL UNIQUE,
  display_name VARCHAR(120) NOT NULL,
  password_hash VARCHAR(255) NOT NULL,
  password_salt VARCHAR(255) NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS projects (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(160) NOT NULL,
  slug VARCHAR(160) NOT NULL UNIQUE,
  source_locale_code VARCHAR(20) NOT NULL,
  created_by BIGINT UNSIGNED NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_projects_creator FOREIGN KEY (created_by) REFERENCES users(id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS project_members (
  project_id BIGINT UNSIGNED NOT NULL,
  user_id BIGINT UNSIGNED NOT NULL,
  role ENUM('manager','translator','reviewer') NOT NULL,
  PRIMARY KEY(project_id,user_id),
  CONSTRAINT fk_pm_project FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
  CONSTRAINT fk_pm_user FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS locales (
  code VARCHAR(20) PRIMARY KEY,
  name VARCHAR(80) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS project_locales (
  project_id BIGINT UNSIGNED NOT NULL,
  locale_code VARCHAR(20) NOT NULL,
  is_source TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY(project_id,locale_code),
  CONSTRAINT fk_pl_project FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
  CONSTRAINT fk_pl_locale FOREIGN KEY(locale_code) REFERENCES locales(code) ON DELETE RESTRICT
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS source_versions (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  project_id BIGINT UNSIGNED NOT NULL,
  version_name VARCHAR(120) NOT NULL,
  imported_by BIGINT UNSIGNED NOT NULL,
  imported_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE(project_id,version_name),
  CONSTRAINT fk_sv_project FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
  CONSTRAINT fk_sv_user FOREIGN KEY(imported_by) REFERENCES users(id) ON DELETE RESTRICT
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS source_strings (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  project_id BIGINT UNSIGNED NOT NULL,
  key_path VARCHAR(500) NOT NULL,
  source_text TEXT NOT NULL,
  status ENUM('active','deleted') NOT NULL DEFAULT 'active',
  current_source_version_id BIGINT UNSIGNED NOT NULL,
  updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE(project_id,key_path),
  INDEX idx_source_project_status(project_id,status),
  CONSTRAINT fk_ss_project FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
  CONSTRAINT fk_ss_version FOREIGN KEY(current_source_version_id) REFERENCES source_versions(id) ON DELETE RESTRICT
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS source_string_versions (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  source_version_id BIGINT UNSIGNED NOT NULL,
  string_id BIGINT UNSIGNED NOT NULL,
  key_path VARCHAR(500) NOT NULL,
  source_text TEXT NOT NULL,
  UNIQUE(source_version_id,string_id),
  CONSTRAINT fk_ssv_version FOREIGN KEY(source_version_id) REFERENCES source_versions(id) ON DELETE CASCADE,
  CONSTRAINT fk_ssv_string FOREIGN KEY(string_id) REFERENCES source_strings(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS source_changes (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  source_version_id BIGINT UNSIGNED NOT NULL,
  string_id BIGINT UNSIGNED NOT NULL,
  change_type ENUM('new','changed','deleted','restored') NOT NULL,
  old_value TEXT NULL,
  new_value TEXT NULL,
  CONSTRAINT fk_sc_version FOREIGN KEY(source_version_id) REFERENCES source_versions(id) ON DELETE CASCADE,
  CONSTRAINT fk_sc_string FOREIGN KEY(string_id) REFERENCES source_strings(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- value NULL = khóa đã được phân công nhưng chưa có nội dung dịch (hiển thị là 'missing').
CREATE TABLE IF NOT EXISTS translations (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  string_id BIGINT UNSIGNED NOT NULL,
  locale_code VARCHAR(20) NOT NULL,
  value TEXT NULL,
  status ENUM('draft','translated','needs_review','approved') NOT NULL DEFAULT 'draft',
  assigned_to BIGINT UNSIGNED NULL,
  row_version INT NOT NULL DEFAULT 1,
  updated_by BIGINT UNSIGNED NOT NULL,
  updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE(string_id,locale_code),
  INDEX idx_translation_locale_status(locale_code,status),
  CONSTRAINT fk_t_string FOREIGN KEY(string_id) REFERENCES source_strings(id) ON DELETE CASCADE,
  CONSTRAINT fk_t_locale FOREIGN KEY(locale_code) REFERENCES locales(code) ON DELETE RESTRICT,
  CONSTRAINT fk_t_assignee FOREIGN KEY(assigned_to) REFERENCES users(id) ON DELETE SET NULL,
  CONSTRAINT fk_t_updated_by FOREIGN KEY(updated_by) REFERENCES users(id) ON DELETE RESTRICT
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS translation_comments (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  translation_id BIGINT UNSIGNED NOT NULL,
  user_id BIGINT UNSIGNED NOT NULL,
  body TEXT NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_tc_translation FOREIGN KEY(translation_id) REFERENCES translations(id) ON DELETE CASCADE,
  CONSTRAINT fk_tc_user FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE RESTRICT
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS translation_history (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  translation_id BIGINT UNSIGNED NOT NULL,
  user_id BIGINT UNSIGNED NOT NULL,
  old_value TEXT NULL,
  new_value TEXT NULL,
  action VARCHAR(40) NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_th_translation FOREIGN KEY(translation_id) REFERENCES translations(id) ON DELETE CASCADE,
  CONSTRAINT fk_th_user FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE RESTRICT
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS reviews (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  translation_id BIGINT UNSIGNED NOT NULL,
  reviewer_id BIGINT UNSIGNED NOT NULL,
  decision ENUM('approved','rejected') NOT NULL,
  note TEXT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_r_translation FOREIGN KEY(translation_id) REFERENCES translations(id) ON DELETE CASCADE,
  CONSTRAINT fk_r_reviewer FOREIGN KEY(reviewer_id) REFERENCES users(id) ON DELETE RESTRICT
) ENGINE=InnoDB;

-- Release là snapshot bất biến: nội dung nằm trong release_files.content_json,
-- sửa bản dịch sau khi phát hành không làm thay đổi release cũ.
CREATE TABLE IF NOT EXISTS releases (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  project_id BIGINT UNSIGNED NOT NULL,
  version_name VARCHAR(120) NOT NULL,
  source_version_id BIGINT UNSIGNED NULL,
  note TEXT NULL,
  key_count INT NOT NULL DEFAULT 0,
  published_by BIGINT UNSIGNED NOT NULL,
  published_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE(project_id,version_name),
  CONSTRAINT fk_rel_project FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE,
  CONSTRAINT fk_rel_source_version FOREIGN KEY(source_version_id) REFERENCES source_versions(id) ON DELETE SET NULL,
  CONSTRAINT fk_rel_user FOREIGN KEY(published_by) REFERENCES users(id) ON DELETE RESTRICT
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS release_files (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  release_id BIGINT UNSIGNED NOT NULL,
  locale_code VARCHAR(20) NOT NULL,
  is_source TINYINT(1) NOT NULL DEFAULT 0,
  file_name VARCHAR(190) NOT NULL,
  content_json LONGTEXT NOT NULL,
  sha256 CHAR(64) NOT NULL,
  key_count INT NOT NULL DEFAULT 0,
  UNIQUE(release_id,locale_code),
  CONSTRAINT fk_rf_release FOREIGN KEY(release_id) REFERENCES releases(id) ON DELETE CASCADE,
  CONSTRAINT fk_rf_locale FOREIGN KEY(locale_code) REFERENCES locales(code) ON DELETE RESTRICT
) ENGINE=InnoDB;
