# Đóng góp cho Translation Manager

1. Tạo issue mô tả yêu cầu, bước tái hiện và kết quả mong đợi. Dùng dữ liệu giả; không gửi mật khẩu/JWT vào issue.
2. Tạo nhánh riêng cho một thay đổi. Tách giao diện PHP/JS, controller, service nghiệp vụ và SQL; không đưa query nghiệp vụ vào frontend.
3. Khi đổi API, cập nhật cả DTO, backend độc lập/backend đi kèm, gateway và hướng dẫn. Giữ payload cũ nếu không có quyết định thay đổi hợp đồng.
4. Chạy kiểm thử trên CSDL demo/tạm, không dùng CSDL chính để chạy bộ test tạo dữ liệu QA. Trong thư mục Frontend, lệnh `scripts/test-all.ps1` chuyển môi trường chạy sang demo 3307; dùng `scripts/start-xampp.ps1` để quay lại 3306 sau đó.
5. Với import/dịch/review/release, kiểm tra quyền trực tiếp qua API, rollback, xung đột row_version, biến thiếu/thừa và gói cũ bất biến.
6. Mở pull request với mô tả thay đổi, test đã chạy, ảnh giao diện nếu có và issue liên quan. Nhờ thành viên khác review trước khi hợp nhất.
7. Không commit `.local`, `.env.local`, tệp khóa, mật khẩu, CSDL đang chạy hoặc bin/obj. Chỉ đưa cấu hình mẫu và JSON giả có nguồn rõ ràng.

Trước khi nộp: điền bảng ba thành viên trong README, thống nhất LICENSE, kiểm tra nguồn/thư viện tái sử dụng, cấp quyền đọc cho giảng viên, tạo release từ commit cuối và thử cài lại gói ZIP trên môi trường sạch.
