# Cơ sở dữ liệu Translation Manager

Ngày bàn giao: 26/10/2026. Dùng MariaDB/XAMPP, bộ mã `utf8mb4`.

## Các file trong gói bàn giao

| File | Dùng khi nào |
| --- | --- |
| `01_translation_manager_demo.sql` | Import vào database demo mới, có đủ cấu trúc và dữ liệu để trình diễn. File tạo database `translation_manager_demo`. |
| `02_upgrade_existing_database.sql` | Nâng cấp database `translation_manager` đang có 15 bảng như ảnh: cập nhật cột, thêm 2 bảng Admin và danh mục ngôn ngữ. |
| `03_setup_existing_admin_and_languages.sql` | Sau bước 02, chọn tài khoản đã đăng ký để cấp Admin đầu tiên và chọn dự án cần gắn 8 ngôn ngữ. |
| `04_schema_only.sql` | Tạo database `translation_manager` mới chỉ có cấu trúc, không có tài khoản/dự án demo. |
| `05_locale_catalog.sql` | Danh mục 11 ngôn ngữ cho database mới; không tự gắn vào dự án. |
| `verification.json` | Kết quả kiểm tra xuất/import và nâng cấp. |

**Chọn một cách cài đặt:** dùng bản demo ở file 01, nâng cấp database hiện có bằng 02 + 03, hoặc tạo database mới bằng 04 + 05. Với database đang có dữ liệu, export một bản sao trước khi nâng cấp. File 01 dùng cho database demo trống; không dùng nó để ghép dữ liệu vào database cũ.

## Import demo bằng phpMyAdmin

1. Bật MySQL trong XAMPP và mở phpMyAdmin.
2. Chọn **Import / Nhập**, chọn `01_translation_manager_demo.sql`, bộ mã `utf-8`, rồi thực hiện. File tự tạo database `translation_manager_demo`; nếu tên này đã được sử dụng, chọn tên database mới và sửa hai câu `CREATE DATABASE` / `USE` trong bản SQL trước khi import.
3. Cấu hình Backend kết nối đúng database vừa import. Nếu dùng MySQL XAMPP cổng 3306:

```text
Server=127.0.0.1;Port=3306;Database=translation_manager_demo;User ID=root;Password=;
```

Đặt chuỗi trên tại `ConnectionStrings:Default` của Backend đang chạy, điền mật khẩu MySQL thực tế nếu có, rồi khởi động lại Backend bằng F5. Bộ demo có sẵn trong workspace vẫn dùng MariaDB riêng ở cổng 3307; file bàn giao không tự thay đổi cấu hình hoặc dữ liệu XAMPP 3306.

### Tài khoản demo

Mật khẩu chung **chỉ dùng cho dữ liệu demo**: `Demo@2026Admin!`

| Vai trò | Email |
| --- | --- |
| Admin hệ thống | admin.demo@tm.local |
| Manager dự án | manager.demo@tm.local |
| Translator | translator.demo@tm.local |
| Reviewer | reviewer.demo@tm.local |
| Người dùng chưa tham gia dự án | user.demo@tm.local |
| Tài khoản đang khóa | locked.demo@tm.local |

Tài khoản đang khóa bị từ chối đăng nhập. Bản xuất chứa hash/salt mật khẩu tương thích Backend. JWT, khóa ký JWT và phiên PHP không nằm trong SQL.

Dự án mẫu: **Demo · Cổng thông tin sinh viên**, nguồn tiếng Việt, 10 khóa nguồn, 10 ngôn ngữ đích. Có bản dịch en/zh, các trạng thái nháp/chờ duyệt/cần sửa/đã duyệt, bình luận, lịch sử và gói phát hành `1.0.0`. Tám ngôn ngữ ja/ko/fr/de/es/pt/ru/th mới có 80 bản dịch chưa nhập. Bản phát hành cũ giữ nguyên vi/en/zh.

## Nâng cấp 15 bảng trong ảnh

1. Chọn database hiện tại trong phpMyAdmin. Trong file 02, dòng `USE translation_manager;` phải khớp tên database cần nâng cấp.
2. Import/chạy file 02. Sau đó có tổng cộng **17 bảng**. Ba cột mới trong `users`: `system_role`, `is_active`, `security_version`.
3. Mở file 03. Xem danh sách tài khoản và dự án bằng hai câu SELECT đầu file. Sửa:

```sql
SET @admin_email = 'email_tai_khoan_da_dang_ky_cua_ban';
SET @project_id = 1; -- Thay 1 bằng ID dự án đã kiểm tra.
```

4. Chạy file 03. File chỉ cấp Admin đầu tiên nếu hệ thống chưa có Admin; những Admin sau được cấp qua giao diện quản trị. Khi giữ giá trị mặc định email mẫu và `NULL`, file không chọn tài khoản hay dự án nào để thay đổi.
5. Khởi động lại Backend, đăng xuất rồi đăng nhập bằng tài khoản vừa cấp quyền.

File 02 không xóa các dòng dữ liệu hiện có và có thể chạy lại trên MariaDB/XAMPP. Các cột mới nhận giá trị mặc định khi schema cũ chưa có. SQL dùng `ADD COLUMN IF NOT EXISTS` của MariaDB. Ảnh chỉ có tên bảng; nếu database từng được sửa cấu trúc khác schema của dự án thì cần đối chiếu `SHOW CREATE TABLE` trước khi áp dụng.

## Mỗi bảng tương ứng chức năng nào?

| Bảng | Nội dung chính | Chức năng trên web |
| --- | --- | --- |
| `users` | Tài khoản, tên/email, hash/salt mật khẩu, quyền hệ thống, trạng thái khóa, phiên bản cập nhật tài khoản. | Đăng ký, đăng nhập, Admin quản lý tài khoản/cấp quyền/khóa mở. |
| `projects` | Tên, slug, ngôn ngữ nguồn, người tạo, ngày tạo dự án. | Danh sách, tạo/sửa/xóa dự án; Admin quản lý toàn bộ dự án. |
| `project_members` | Liên kết người dùng với dự án, vai trò `manager` / `translator` / `reviewer`. | Thành viên, phân quyền trong từng dự án. |
| `locales` | Danh mục mã và tên ngôn ngữ dùng chung. | Thêm/chọn ngôn ngữ. Chỉ có trong danh mục chưa có nghĩa đã thuộc dự án. |
| `project_locales` | Ngôn ngữ được gắn vào từng dự án, cờ nguồn/đích, tên hiển thị riêng. | Trang Ngôn ngữ, bộ chọn ngôn ngữ dịch và kiểm tra phát hành. |
| `source_versions` | Mỗi lần nhập JSON nguồn, tên phiên bản, người nhập và thời điểm. | Nhập nguồn, danh sách phiên bản nguồn. |
| `source_strings` | Khóa và nội dung nguồn hiện tại, trạng thái đang dùng/đã xóa. | Translation Keys, tìm kiếm nguồn, nội dung nguồn trong màn hình dịch. |
| `source_string_versions` | Snapshot khóa/nội dung của từng phiên bản nguồn. | Xem/so sánh phiên bản, kiểm tra nguồn của bản phát hành. |
| `source_changes` | Khóa mới, đổi, xóa, khôi phục; giá trị trước/sau khi nhập nguồn. | Thống kê và chi tiết thay đổi giữa các phiên bản nguồn. |
| `translations` | Bản dịch theo khóa/ngôn ngữ, trạng thái, người được giao, người sửa, `row_version`. | Soạn thảo, lưu nháp, gửi duyệt, phân công, lọc và phát hiện xung đột. |
| `translation_comments` | Nội dung bình luận, tác giả và thời điểm trên một bản dịch. | Bình luận/thảo luận. |
| `translation_history` | Hoạt động dịch/phân công/đánh giá, người thao tác, giá trị trước/sau. | Lịch sử từng bản dịch và lịch sử tổng hợp dự án. |
| `reviews` | Quyết định duyệt/từ chối, Reviewer, ghi chú và thời điểm. | Trang Đánh giá, lý do từ chối, nhận xét đánh giá gần nhất. |
| `releases` | Phiên bản phát hành, nguồn được dùng, ghi chú, người/ngày phát hành, số khóa. | Tạo và liệt kê bản phát hành. |
| `release_files` | Nội dung JSON đã chốt cho mỗi ngôn ngữ, tên file, SHA-256, số khóa. | Tải JSON/ZIP; giữ nội dung bản phát hành cũ khi bản dịch thay đổi. |
| `system_admin_guard` | Một hàng khóa kỹ thuật có `id=1`, dùng trong giao dịch quản trị. | Ngăn các thao tác đồng thời làm mất Admin/Manager đang hoạt động cuối cùng. Không phải bảng danh sách Admin. |
| `admin_audit` | Admin thực hiện, hành động, đối tượng, nội dung thay đổi và thời điểm. | Nhật ký quản trị hệ thống; vẫn còn khi dự án bị xóa. |

Ảnh có 15 bảng đầu; hai bảng cuối là phần bổ sung cho Admin.

## Các điểm phân quyền và dữ liệu cần phân biệt

- `users.system_role`: quyền hệ thống, chỉ `admin` hoặc `user`.
- `project_members.role`: quyền riêng cho từng dự án. Một người có thể làm Manager ở dự án A và Reviewer ở dự án B. Admin không tự trở thành thành viên của mọi dự án.
- `translations.row_version`: phiên bản để phát hiện hai người cùng sửa bản dịch. `users.security_version` phục vụ tránh hai Admin ghi đè thay đổi tài khoản, không phải số phiên đăng nhập.
- `translations.status`: `draft` = nháp, `translated` = chờ duyệt, `needs_review` = cần sửa/nguồn đã thay đổi, `approved` = đã duyệt. `missing` là trạng thái hiển thị khi chưa có bản dịch hoặc `value IS NULL`.
- Thêm ngôn ngữ vào `locales` là thêm danh mục. Gắn vào `project_locales` mới làm ngôn ngữ xuất hiện trong dự án. Hệ thống không tự tạo nội dung dịch.
- Dashboard và lịch sử tổng hợp lấy dữ liệu từ các bảng nghiệp vụ, không cần bảng Dashboard hay bảng lịch sử tổng hợp riêng.
- Khi phát hành, nội dung được chốt vào `release_files`; gói ZIP được Backend tạo từ các nội dung này. File SQL chứa đủ nội dung JSON của release demo.
