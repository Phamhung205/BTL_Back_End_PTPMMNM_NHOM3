USE translation_manager;

INSERT IGNORE INTO locales(code,name) VALUES
('vi','Tiếng Việt'),
('en','English'),
('zh','中文');

-- Tài khoản không seed mật khẩu để tránh đưa mật khẩu giả cứng vào source.
-- Hãy POST /api/auth/register để tạo người dùng đầu tiên.
