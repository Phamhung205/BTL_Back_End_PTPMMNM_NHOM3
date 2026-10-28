# Translation Manager Backend

Cập nhật 22/10/2026: Frontend PHP đã bổ sung ba màn hình Ngôn ngữ / Phiên bản nguồn / Translation Keys. Nhập nguồn hỗ trợ `{versionName,rawJson}` để kiểm tra khóa bị lặp và kích thước tệp gốc khi đi qua gateway PHP. Payload cũ `{versionName,data}` tiếp tục được hỗ trợ; chỉ gửi một trong `rawJson`/`data`. Nội dung nguồn tối đa 2 MB UTF-8; HTTP envelope tối đa 4 MB + 8 KB để chứa ký tự escape. SQL không tự động chạy; CSDL cũ của ba module này cần `database/migrations/003_manh_source_management.sql`.

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
PUT  /api/projects/{projectId}/locales/{code}
DELETE /api/projects/{projectId}/locales/{code}
GET  /api/projects/{projectId}/translation-keys?q=&status=all

POST /api/projects/{projectId}/source-versions/import
GET  /api/projects/{projectId}/source-versions
GET  /api/projects/{projectId}/source-versions/{versionId}/changes

GET  /api/projects/{projectId}/translations?locale=en&q=&status=&assignee=
GET  /api/projects/{projectId}/translations/stats
POST /api/projects/{projectId}/translations
POST /api/projects/{projectId}/translations/import
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

## Module Language / Translation Key / Source Version (Bùi Đình Mạnh)

### Khởi tạo hoặc nâng cấp CSDL

- CSDL mới: chạy `database/schema.sql`, sau đó `database/seed.sql`.
- CSDL đã có: giữ dữ liệu hiện tại, chạy `database/migrations/003_manh_source_management.sql`. Nếu đang dùng schema rất cũ, chạy migration 002 trước 003.
- Migration 003 bổ sung `project_locales.display_name` và dùng `utf8mb4_bin` cho đường dẫn khóa trong nguồn và snapshot. Có thể chạy lại; không xóa bản dịch hoặc release.
- `NuGet.Config` chỉ dùng nguồn nuget.org và bỏ fallback riêng của Visual Studio, giúp restore được trên máy không cài Visual Studio.

### API và quyền

Tất cả route dưới đây có prefix `/api/projects/{projectId}` và yêu cầu JWT. Thành viên dự án được đọc; chỉ manager được thêm/sửa/gỡ ngôn ngữ và import nguồn.

| Method / route | Dữ liệu |
| --- | --- |
| `GET /locales` | `[{code,name,isSource}]`; tên ưu tiên theo dự án |
| `POST /locales` | `{localeCode}`; hỗ trợ mã `vi`, `en`, `zh`, `en-US` |
| `PUT /locales/{code}` | `{name}`; tên 1–80 ký tự, chỉ thay đổi trong dự án này |
| `DELETE /locales/{code}` | 204 khi thành công; 409 nếu đã có bản dịch/phân công hoặc release; giữ nguyên toàn bộ dữ liệu khi bị từ chối |
| `GET /translation-keys?q=&status=all` | `[{id,key,sourceText,status,sourceVersionId,updatedAt}]`; status: `all`, `active`, `deleted` |
| `GET /source-versions` | Mới nhất trước; `id,versionName,importedAt,importedBy,importedById,keyCount,added,changed,restored,deleted,unchanged` |
| `POST /source-versions/import` | `{versionName,data}` → `versionId,versionName,total,added,changed,unchanged,deleted` |
| `GET /source-versions/{versionId}/changes` | Thay đổi so với lần import trước: `[{type,key,oldValue,newValue}]` |
| `GET /source-versions/{versionId}/changes?compareToVersionId={baseId}` | So sánh hai snapshot bất kỳ trong cùng dự án; kiểu `new/changed/deleted` so với base |

Không được sửa/gỡ ngôn ngữ nguồn. Không xóa locale dùng chung khỏi catalog. Mã ngôn ngữ được chuẩn hóa chữ thường; mã hoặc tên không hợp lệ trả 400. Thêm trùng trả 409. Phiên bản không tồn tại hoặc thuộc dự án khác trả 404; người ngoài dự án trả 403.

### Quy ước JSON và cập nhật nguồn

- JSON object lồng nhau, giá trị chuỗi, tối đa 2 MB; tên phiên bản 1–120 ký tự và không trùng trong dự án.
- Khóa phân biệt hoa/thường và dấu: `Title`, `title`, `ten`, `tên` là bốn khóa độc lập.
- Tên khóa không rỗng, không có khoảng trắng đầu/cuối, cho phép dấu chấm làm dấu phân cấp (a.b tương đương a → b), từ chối đường dẫn trùng hoặc xung đột cha/con; đường dẫn tối đa 500 ký tự, chuỗi tối đa 10000 ký tự. Không hỗ trợ mảng, số, boolean, null, object rỗng hoặc thuộc tính JSON bị lặp. Giới hạn độ sâu theo System.Text.Json mặc định là 64.
- Mỗi tệp là **toàn bộ snapshot nguồn**. Khóa vắng mặt được đánh dấu `deleted`; khóa quay lại dùng lại ID và ghi change `restored`. Lịch sử, snapshot và nội dung bản dịch được giữ nguyên.
- Nhập lại cùng nội dung dưới tên phiên bản mới không nhân đôi khóa và không thay trạng thái/row_version của bản dịch không liên quan.
- Nguồn sửa/xóa/khôi phục làm tăng `row_version` của mọi bản dịch liên quan. Nháp vẫn là `draft`; các trạng thái khác thành `needs_review`. Client đang giữ bản nháp cũ phải tải lại trước khi lưu.
- Import, thay đổi locale và publish được tuần tự hóa bằng khóa dự án trong transaction; không tạo snapshot trộn từ hai lần import đồng thời.
- Kiểm tra release đối chiếu ngôn ngữ nguồn, snapshot nguồn mới nhất và tiến độ duyệt. Nguồn không khớp snapshot hoặc bản dịch chưa duyệt đủ sẽ bị chặn; release đã chốt giữ nguyên.

### Kiểm thử API local

Backend và MariaDB phải đang chạy; dùng một CSDL kiểm thử. Test tạo tài khoản/dự án giả mới theo mỗi lần chạy, không dọn dữ liệu dự án có sẵn.

```powershell
node tests/api/manh-source-management.test.mjs http://localhost:5088
node tests/api/duc-translation-release.test.mjs http://localhost:5088
node tests/api/duc-import-security.test.mjs http://localhost:5088
```

Bộ Mạnh gồm 10 nhóm: quyền, locale độc lập giữa dự án, JSON sai/giới hạn tệp, hơn 100 khóa, Unicode/hoa-thường, import lại/trùng phiên bản, draft conflict khi nguồn đổi, xóa/khôi phục, diff hai snapshot, import đồng thời và release cũ giữ nguyên.

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
- Quyền: manager sửa mọi bản dịch; translator chỉ thấy và sửa khóa Manager đã giao cho mình; manager phân công và nhập bản dịch từ tệp; manager + reviewer duyệt/từ chối; mọi thành viên bình luận; chỉ manager phát hành.
- Lưu bản dịch chỉ nhận `stringId` của khóa nguồn **đang dùng trong đúng dự án** ở URL; khóa của dự án khác, khóa đã xóa hoặc không tồn tại trả 404 và không ghi dữ liệu.

### Nhập bản dịch có sẵn

`POST /api/projects/{projectId}/translations/import` (Manager), body `{ localeCode, rawJson, status, overwrite }` với `rawJson` là JSON lồng nhau hoặc khóa có dấu chấm (cùng quy ước với nhập nguồn, tối đa 2 MB). Trong một giao dịch:

- Khóa đối chiếu với chuỗi nguồn đang dùng; khóa không có trong nguồn, nội dung rỗng hoặc quá 10.000 ký tự bị bỏ qua và liệt kê trong báo cáo.
- `status`: `translated` (mặc định, chờ duyệt) hoặc `draft`; **không tự duyệt**. Bản thiếu/thừa biến luôn lưu nháp và được liệt kê trong `placeholderIssues`.
- Nhập lại cùng nội dung → `unchanged`, giữ nguyên trạng thái (kể cả đã duyệt / cần kiểm tra lại). Bản đang có nội dung khác chỉ bị thay khi `overwrite: true`; khi đó `row_version` tăng nên người đang sửa từ bản cũ nhận 409.
- Kết quả: `created`, `updated`, `unchanged`, `keptExisting`, `notInSource`, `empty`, `tooLong`, `placeholderMismatch` và danh sách khóa (tối đa 100 mỗi loại). Lịch sử ghi thao tác `imported`.

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

`node tests/api/duc-import-security.test.mjs http://localhost:5088`: 5 nhóm — hồi quy không ghi bản dịch sang khóa của dự án khác/đã xóa/không tồn tại; nhập bản dịch (báo cáo, không tự duyệt, sai biến lưu nháp); nhập lại không trùng, giữ/ghi đè bản đang có và 409 cho người sửa từ bản cũ; quyền và JSON không hợp lệ.

Bộ 5 phép thử bắt buộc + 2 tình huống của đề tài chạy trên dữ liệu 3 website mẫu nằm ở Frontend: `tests/required-tests.mjs` (xem `docs/PHEP_THU_BAT_BUOC.md`).

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

## Hoàn thiện giao diện và API hỗ trợ — 25/10/2026

Frontend đã nối đầy đủ Translation/Review/Release, phân công hàng loạt, bình luận, xử lý xung đột rowVersion và tải JSON/ZIP qua phiên PHP có CSRF. Danh sách bản dịch trả canEdit theo quyền hiện tại của người đăng nhập.

API bổ sung trong WorkspaceController:
- GET /api/dashboard/stats: projects, managedProjects, languages, pendingTranslations; chỉ thống kê các dự án hiện tham gia.
- GET /api/projects/{projectId}/activity?page=1: {items,page,hasMore}; tối đa 50 hoạt động mỗi trang, gồm nguồn, bản dịch, phân công, đánh giá, bình luận và phát hành.

Nếu CSDL MariaDB đã có đủ bảng nhưng dùng schema cũ, cần migration 002 rồi 003. Controller mới không thêm bảng. Lượt này migration chỉ được chạy trên database kiểm thử riêng ở cổng 3307, không sửa database XAMPP cổng 3306.

Hai Backend build thành công. Bộ Translation/Release đạt 13/13 nhóm, Source/Locale đạt 10/10 nhóm; luồng đầy đủ qua gateway đạt 53/53 kiểm tra trên MariaDB kiểm thử. Chi tiết sử dụng ở docs/FEATURES.md trong Frontend.

## Admin hệ thống và demo

Migration 004_system_admin.sql bổ sung users.system_role/is_active/security_version, system_admin_guard và admin_audit. API /api/admin/* yêu cầu policy SystemAdmin; JWT đọc lại quyền và trạng thái người dùng trên mỗi request. GET /api/auth/me trả danh tính hiện tại. Vai trò Admin độc lập với manager/translator/reviewer của dự án.

Admin quản lý tài khoản, khóa/mở, vai trò hệ thống, toàn bộ dự án/thành viên và đọc nhật ký. Bảo vệ Admin đang hoạt động cuối cùng và Manager đang hoạt động cuối cùng. Không tự cấp quyền Admin qua đăng ký công khai.

Admin đầu tiên được tạo bằng CLI --bootstrap-admin với BootstrapAdmin__Email, BootstrapAdmin__DisplayName, BootstrapAdmin__Password (12+ ký tự), connection string và JWT phù hợp. Lệnh không cấp thêm Admin nếu đã có Admin, không có bootstrap API công khai.

Frontend có prepare-demo.ps1 và test-all.ps1. Bộ demo dùng translation_manager_demo ở MariaDB riêng cổng 3307. Hướng dẫn và tài khoản demo: docs/SETUP.md trong Frontend. Không áp dụng mật khẩu demo vào hệ thống thật.


## Bổ sung ngày 28/10/2026

POST /api/projects nhận thêm targetLocaleCodes và members (email hoặc userId, role); tạo dự án, ngôn ngữ và thành viên trong cùng transaction. Payload cũ vẫn hoạt động. GET /api/projects/{id}/releases/report trả báo cáo điều kiện hiện tại và lịch sử phát hành. --create-local-admin chỉ tạo Admin bằng CLI với CSDL localhost; thông tin lấy từ biến BootstrapAdmin, không có endpoint công khai cấp Admin.

Frontend có hướng dẫn và ví dụ tại docs/HUONG_DAN_NHOM.md, tests/project-onboarding.mjs và examples/website. Bản dịch mẫu có hai dự án 1.010 / 170 khóa, en/vi/zh, hai source version và hai release phục vụ tình huống 2.

## Import bản dịch JSON — cập nhật 29/10/2026

Manager gọi POST /api/projects/{projectId}/translations/import với localeCode, rawJson, status (draft/translated) và overwrite. Bản nhập không tự duyệt. Báo cáo trả created/updated/unchanged/keptExisting, empty/notInSource/tooLong, placeholderMismatch (sai biến trong tệp), savedDraft (số bản sai biến thực sự được ghi và lưu nháp), kèm danh sách khóa.

Khóa sai dự án/đã xóa/không tồn tại không được ghi. Cùng nội dung giữ nguyên trạng thái; đổi nội dung khi overwrite=true thì cần duyệt lại. Giá trị trên 10.000 ký tự bị bỏ qua theo khóa trong import bản dịch; import nguồn vẫn từ chối toàn bộ tài liệu nếu có giá trị quá dài. Giới hạn tài liệu JSON vẫn là 2 MB.

Kiểm thử hồi quy: tests/api/duc-import-security.test.mjs (6 nhóm). Hướng dẫn chạy, dữ liệu đủ 2 dự án/3 ngôn ngữ/ít nhất 100 khóa và hai tình huống nằm trong docs/DEMO_NHOM3.md của repo frontend.
