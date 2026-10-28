-- Read-only customer API keys, scoped to a single immutable release.
-- The API stores only a SHA-256 hash; raw keys are returned once when created/rotated.
CREATE TABLE IF NOT EXISTS release_api_keys (
  release_id BIGINT UNSIGNED PRIMARY KEY,
  key_hash CHAR(64) NOT NULL,
  key_prefix VARCHAR(24) NOT NULL,
  created_by BIGINT UNSIGNED NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  revoked_at TIMESTAMP NULL,
  CONSTRAINT fk_release_api_key_release FOREIGN KEY(release_id) REFERENCES releases(id) ON DELETE CASCADE,
  CONSTRAINT fk_release_api_key_user FOREIGN KEY(created_by) REFERENCES users(id) ON DELETE RESTRICT
) ENGINE=InnoDB;
