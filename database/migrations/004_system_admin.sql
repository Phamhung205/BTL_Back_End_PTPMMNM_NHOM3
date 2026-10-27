-- MariaDB/XAMPP. Existing users stay ordinary, active users.
USE translation_manager;
ALTER TABLE users
  ADD COLUMN IF NOT EXISTS system_role ENUM('user','admin') NOT NULL DEFAULT 'user',
  ADD COLUMN IF NOT EXISTS is_active TINYINT(1) NOT NULL DEFAULT 1,
  ADD COLUMN IF NOT EXISTS security_version INT NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS system_admin_guard (id TINYINT UNSIGNED PRIMARY KEY) ENGINE=InnoDB;
INSERT IGNORE INTO system_admin_guard(id) VALUES(1);
CREATE TABLE IF NOT EXISTS admin_audit (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  actor_id BIGINT UNSIGNED NULL,
  action VARCHAR(60) NOT NULL,
  target_type VARCHAR(30) NOT NULL,
  target_id BIGINT UNSIGNED NULL,
  summary TEXT NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_admin_audit_date(created_at,id),
  CONSTRAINT fk_admin_audit_actor FOREIGN KEY(actor_id) REFERENCES users(id) ON DELETE SET NULL
) ENGINE=InnoDB;
