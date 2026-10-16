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

GET  /api/projects/{projectId}/translations?locale=en&q=&status=
POST /api/projects/{projectId}/translations
PUT  /api/projects/{projectId}/translations/{translationId}/assign
POST /api/projects/{projectId}/translations/{translationId}/comments
POST /api/projects/{projectId}/translations/{translationId}/approve
POST /api/projects/{projectId}/translations/{translationId}/reject

GET  /api/projects/{projectId}/releases/validate
GET  /api/projects/{projectId}/releases
POST /api/projects/{projectId}/releases
GET  /api/projects/{projectId}/releases/{releaseId}/files/{locale}
```

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
├── database/
├── storage/releases/
├── Program.cs
├── TranslationManager.Api.csproj
├── appsettings.json
├── Dockerfile
├── web.config
└── README.md
```
