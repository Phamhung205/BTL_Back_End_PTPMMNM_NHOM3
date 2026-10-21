# Translation Manager Backend

Backend riêng cho BTL **Xây dựng hệ thống quản lý bản dịch và phát hành gói ngôn ngữ cho phần mềm**.

## Công nghệ

- C# / ASP.NET Core Web API (`net10.0`)
- MySQL/MariaDB chạy bằng **XAMPP** (mặc định `127.0.0.1:3306`)
- `MySqlConnector`
- JWT Authentication
- Không chứa PHP và không chứa giao diện Frontend.

> Lưu ý học phần: tài liệu BTL gốc nêu PHP + PostgreSQL/MySQL là yêu cầu kỹ thuật chung. Nếu dùng C# cho backend, nhóm nên xin giảng viên duyệt công nghệ khác trước khi nộp chính thức.

## Chức năng backend hiện có

- Đăng ký / đăng nhập, JWT.
- Project và thành viên theo vai trò `manager`, `translator`, `reviewer`.
- Gắn nhiều locale vào project.
- Nhập JSON nguồn dạng object lồng nhau; flatten thành key path.
- Nhận biết key mới, thay đổi, xóa, khôi phục theo source version.
- Khi source thay đổi, translation liên quan chuyển `needs_review`.
- Danh sách/search/filter translation.
- Lưu draft/translated.
- Kiểm tra placeholder như `{name}`, `{count}`.
- Phân công người dịch, comment.
- Review approve/reject theo quyền.
- Optimistic concurrency bằng `row_version`: hai người sửa cùng bản dịch sẽ trả HTTP 409 thay vì ghi đè.
- Kiểm tra điều kiện phát hành.
- Chốt release và snapshot JSON trong DB; sửa translation hiện tại không thay đổi release cũ.
- Download lại JSON từ release đã chốt.

## CSDL XAMPP

1. Mở XAMPP, Start **MySQL**.
2. Mở phpMyAdmin hoặc MySQL client.
3. Chạy `database/schema.sql`.
4. Chạy `database/seed.sql`.

Connection mặc định:

```text
Server=127.0.0.1
Port=3306
Database=translation_manager
User=root
Password=(trống theo XAMPP mặc định)
```

Nếu MySQL của bạn có mật khẩu, sửa `ConnectionStrings__Default` bằng biến môi trường hoặc `appsettings.json` khi chạy local. Không commit mật khẩu thật.

## Chạy backend

Yêu cầu .NET 10 SDK:

```powershell
dotnet restore
dotnet run
```

Mặc định profile HTTP:

```text
http://localhost:5088
```

Kiểm tra:

```text
GET http://localhost:5088/health
```

## API chính

```text
POST /api/auth/register
POST /api/auth/login

GET  /api/projects
POST /api/projects
GET  /api/projects/{projectId}/members
POST /api/projects/{projectId}/members
GET  /api/projects/{projectId}/locales
POST /api/projects/{projectId}/locales

POST /api/projects/{projectId}/source-versions/import
GET  /api/projects/{projectId}/source-versions/{versionId}/changes

GET  /api/projects/{projectId}/translations?locale=en&q=&status=&assignee=
GET  /api/projects/{projectId}/translations/stats
POST /api/projects/{projectId}/translations
PUT  /api/projects/{projectId}/translations/assign
PUT  /api/projects/{projectId}/translations/{translationId}/assign
GET  /api/projects/{projectId}/translations/{translationId}/comments
POST /api/projects/{projectId}/translations/{translationId}/comments
GET  /api/projects/{projectId}/translations/{translationId}/history
POST /api/projects/{projectId}/translations/{translationId}/approve
POST /api/projects/{projectId}/translations/{translationId}/reject

GET  /api/projects/{projectId}/releases/validate
GET  /api/projects/{projectId}/releases
POST /api/projects/{projectId}/releases
GET  /api/projects/{projectId}/releases/{releaseId}
GET  /api/projects/{projectId}/releases/{releaseId}/files/{locale}
GET  /api/projects/{projectId}/releases/{releaseId}/download
```

## Module Translation / Review / Release (Phạm Thế Đức)

### Vòng đời bản dịch

```text
missing ──lưu nháp──> draft ──gửi review──> translated ──duyệt──> approved
                                               │                    │
                              từ chối (có lý do)│   nguồn thay đổi    │
                                               v                    v
                                          needs_review <────────────┘
```

- `missing` là trạng thái suy ra (chưa có bản dịch, hoặc mới được phân công — `value` NULL).
- Lưu nháp được phép thiếu biến; **gửi review / duyệt** bắt buộc biến `{name}`, `{count}`... khớp chuỗi nguồn, sai trả 400 kèm `details.missing` / `details.extra`.
- Quyền: manager + translator sửa bản dịch (translator không sửa được khóa đã giao cho người khác); manager phân công; manager + reviewer duyệt/từ chối; mọi thành viên bình luận; chỉ manager phát hành.

### Phát hiện xung đột (HTTP 409)

Mỗi bản dịch có `row_version`. Client gửi `expectedRowVersion` = giá trị lúc tải; nếu người khác đã lưu trước, API trả **409** kèm `details.currentValue`, `currentRowVersion`, `updatedBy`, `updatedAt` — không ghi đè âm thầm. Ghi đè chỉ xảy ra khi người dùng chủ động gửi lại với `currentRowVersion`.

### Release

- `POST /releases` chạy trong một transaction (REPEATABLE READ): kiểm tra điều kiện, tạo snapshot JSON lồng nhau cho ngôn ngữ nguồn + từng ngôn ngữ đích, lưu vào `release_files` (nội dung + SHA-256 + số khóa).
- Tên phiên bản: chữ/số/`.`/`-`/`_`, ví dụ `1.0.0`; trùng tên trả 409.
- Release là **bất biến**: sửa bản dịch hoặc import nguồn mới không làm đổi file của release cũ. Khi tải, server kiểm tra lại SHA-256 và gửi header `X-Content-SHA256`.
- `GET /releases/{id}/download` trả gói `.zip` gồm các file JSON + `manifest.json`. Bản sao file cũng được ghi ra `storage/releases/{slug}/{version}/` (chỉ để tiện xem; DB là nguồn chuẩn).

### CSDL

Đã cập nhật `database/schema.sql` (cột `translations.value` cho phép NULL; `releases.source_version_id/note/key_count`; `release_files.is_source/key_count`). Nếu đã tạo DB từ schema cũ, chạy thêm `database/migrations/002_duc_translation_release.sql` (MariaDB/XAMPP).

### Test tự động (Node 18+, không cần npm install)

```powershell
# Backend đang chạy ở http://localhost:5088 và MySQL XAMPP đang bật
node tests/api/duc-translation-release.test.mjs http://localhost:5088
```

13 nhóm test: danh sách/lọc/quyền, placeholder, **conflict khi hai người cùng sửa**, phân quyền, phân công, bình luận, approve/reject, điều kiện phát hành, phát hành JSON lồng nhau + Unicode + SHA-256, **release cũ không đổi** khi sửa bản dịch / import nguồn mới, tải .zip, thống kê. Mỗi lần chạy tạo user/dự án mới nên chạy lại được.

### Dữ liệu demo cho giao diện

```powershell
node tests/api/seed-demo.mjs http://localhost:5088
```

Tạo dự án "Ứng dụng Demo" (vi → en, zh) và 4 tài khoản demo `manager.demo@`, `translator.demo@`, `translator2.demo@`, `reviewer.demo@example.com` (mật khẩu demo `Demo@123`, chỉ dùng local).

## Tách Frontend / Backend

Project này là **Backend độc lập**. Frontend PHP/HTML/Tailwind/JavaScript sẽ nằm ở project khác và chỉ gọi HTTP API của backend.

```text
Frontend
   |
   | HTTP + JSON + JWT
   v
C# ASP.NET Core Backend
   |
   v
MySQL/MariaDB (XAMPP)
```

## Deploy

- `Dockerfile`: thuận tiện cho Northflank/container hosting.
- `web.config`: dùng khi publish ASP.NET Core lên Windows/IIS như hosting hỗ trợ ASP.NET.
- Frontend có thể deploy riêng; backend chỉ cần đặt `FrontendOrigins`, connection string và JWT bằng biến môi trường.

## Cấu trúc

```text
TranslationManager.Backend/
├── Controllers/
├── Data/
├── Dtos/
├── Models/
├── Services/
├── Support/
├── database/            (schema.sql, seed.sql, migrations/)
├── storage/releases/
├── tests/api/           (API test + seed demo, chạy bằng Node)
├── Program.cs
├── TranslationManager.Api.csproj
├── appsettings.json
├── Dockerfile
├── web.config
└── README.md
```
