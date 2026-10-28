-- Shared, anonymous phrase bank. No user or project identifiers are retained.
USE translation_manager;

CREATE TABLE IF NOT EXISTS translation_memory (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  source_locale_code VARCHAR(20) NOT NULL DEFAULT 'vi',
  target_locale_code VARCHAR(20) NOT NULL,
  source_text TEXT NOT NULL,
  target_text TEXT NOT NULL,
  source_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  target_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_translation_memory_pair(target_locale_code, source_hash, target_hash),
  INDEX idx_translation_memory_search(target_locale_code, source_hash),
  CONSTRAINT fk_tm_source_locale FOREIGN KEY(source_locale_code) REFERENCES locales(code) ON DELETE RESTRICT,
  CONSTRAINT fk_tm_target_locale FOREIGN KEY(target_locale_code) REFERENCES locales(code) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Seed the shared bank from non-empty submitted/approved translations. Drafts are
-- excluded so unfinished text and placeholder-invalid drafts are not suggested.
INSERT IGNORE INTO translation_memory(source_locale_code,target_locale_code,source_text,target_text,source_hash,target_hash)
SELECT 'vi', pair.target_locale_code, pair.source_text, pair.target_text,
       SHA2(TRIM(pair.source_text),256), SHA2(TRIM(pair.target_text),256)
FROM (
  SELECT t.locale_code AS target_locale_code, ss.source_text AS source_text, t.value AS target_text
  FROM projects p
  JOIN source_strings ss ON ss.project_id=p.id AND ss.status='active'
  JOIN translations t ON t.string_id=ss.id AND t.locale_code<>'vi'
    AND t.status IN ('translated','approved')
  WHERE p.source_locale_code='vi'

  UNION ALL

  SELECT t.locale_code, vi.value, t.value
  FROM projects p
  JOIN source_strings ss ON ss.project_id=p.id AND ss.status='active'
  JOIN translations vi ON vi.string_id=ss.id AND vi.locale_code='vi'
    AND vi.status IN ('translated','approved')
  JOIN translations t ON t.string_id=ss.id AND t.locale_code<>'vi'
    AND t.status IN ('translated','approved')
  WHERE p.source_locale_code<>'vi'

  UNION ALL

  SELECT p.source_locale_code, vi.value, ss.source_text
  FROM projects p
  JOIN source_strings ss ON ss.project_id=p.id AND ss.status='active'
  JOIN translations vi ON vi.string_id=ss.id AND vi.locale_code='vi'
    AND vi.status IN ('translated','approved')
  WHERE p.source_locale_code<>'vi'
) pair
WHERE TRIM(pair.source_text)<>'' AND TRIM(pair.target_text)<>'';
