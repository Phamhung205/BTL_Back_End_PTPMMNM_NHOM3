-- Chay sau upgrade_existing_database.sql tren MariaDB/XAMPP.
USE translation_manager;
SET NAMES utf8mb4 COLLATE utf8mb4_unicode_ci;

-- A. Chon tai khoan da dang ky tren web va dung du an can gan ngon ngu.
SELECT id,email,display_name,system_role,is_active FROM users ORDER BY id;
SELECT id,name,slug,source_locale_code FROM projects ORDER BY id;

-- Thay email bang tai khoan cua ban. Mac dinh khong cap quyen cho ai.
SET @admin_email = 'THAY_EMAIL_ADMIN_CUA_BAN';
-- Thay NULL bang id du an da kiem tra o truy van tren.
SET @project_id = NULL;

-- B. Khoi tao Admin dau tien. Khoa bao ve va audit cung giao dich.
-- Neu da co Admin, khong tu cap them quyen: dung giao dien Admin.
START TRANSACTION;
-- UPDATE van giu khoa hang den COMMIT, tranh phpMyAdmin them LIMIT vao SELECT.
UPDATE system_admin_guard SET id=id WHERE id=1;
SET @admin_count = (SELECT COUNT(*) FROM users WHERE system_role='admin');

UPDATE users
SET system_role='admin',is_active=1,security_version=security_version+1
WHERE email=@admin_email AND @admin_count=0;
SET @admin_changed = ROW_COUNT();

INSERT INTO admin_audit(actor_id,action,target_type,target_id,summary)
SELECT id,'admin.bootstrapped','user',id,
       'Khởi tạo Admin đầu tiên bằng SQL thiết lập.'
FROM users
WHERE email=@admin_email AND @admin_changed=1;
COMMIT;

SELECT id,email,display_name,system_role,is_active
FROM users WHERE system_role='admin';

-- C. Gan 8 ngon ngu vao dung du an. Khong tao ban ghi trung;
-- khong gan ngon ngu nguon cua du an thanh ngon ngu dich.
INSERT INTO project_locales(project_id,locale_code,is_source)
SELECT p.id,l.code,0
FROM projects p
JOIN locales l ON l.code IN ('ja','ko','fr','de','es','pt','ru','th')
WHERE p.id=@project_id
  AND l.code<>p.source_locale_code
  AND NOT EXISTS (
    SELECT 1 FROM project_locales pl
    WHERE pl.project_id=p.id AND pl.locale_code=l.code
  );

SELECT p.id,p.name,pl.locale_code,COALESCE(pl.display_name,l.name) AS locale_name,
       pl.is_source
FROM projects p
JOIN project_locales pl ON pl.project_id=p.id
JOIN locales l ON l.code=pl.locale_code
WHERE p.id=@project_id
ORDER BY pl.is_source DESC,pl.locale_code;
