namespace TranslationManager.Api.Support;

public sealed record PermissionDefinition(string Code, string Label, string Scope);
public sealed record RoleDefinition(string Code, string Label, string Scope, string Description, string[] Permissions);

/// <summary>Public capability descriptions for the existing, server-enforced roles.</summary>
public static class PermissionCatalog
{
    private static readonly PermissionDefinition[] Definitions =
    [
        new("projects.create", "Tạo dự án của mình", "system"),
        new("projects.read_own", "Xem dự án đang tham gia", "system"),
        new("admin.stats.read", "Xem thống kê hệ thống", "system"),
        new("admin.users.read", "Tìm và xem tài khoản", "system"),
        new("admin.users.create", "Thêm tài khoản", "system"),
        new("admin.users.update", "Sửa thông tin tài khoản", "system"),
        new("admin.users.assign_role", "Cấp hoặc thu quyền Admin", "system"),
        new("admin.users.set_status", "Khóa hoặc mở tài khoản", "system"),
        new("admin.projects.read", "Xem mọi dự án qua Admin", "system"),
        new("admin.projects.update", "Sửa mọi dự án qua Admin", "system"),
        new("admin.projects.delete", "Xóa dự án qua Admin", "system"),
        new("admin.members.manage", "Quản lý thành viên các dự án", "system"),
        new("admin.audit.read", "Xem nhật ký quản trị", "system"),
        new("admin.roles.read", "Xem danh mục vai trò và quyền", "system"),
        new("project.read", "Xem thông tin dự án", "project"),
        new("project.update", "Sửa thông tin dự án", "project"),
        new("project.delete", "Xóa dự án", "project"),
        new("members.read", "Xem thành viên", "project"),
        new("members.manage", "Thêm, sửa vai trò, gỡ thành viên", "project"),
        new("locales.read", "Xem ngôn ngữ", "project"),
        new("locales.manage", "Quản lý ngôn ngữ đích", "project"),
        new("source.read", "Xem nguồn và so sánh phiên bản", "project"),
        new("source.import", "Nhập phiên bản nguồn", "project"),
        new("keys.read", "Xem và tìm khóa", "project"),
        new("translations.read", "Xem và tìm bản dịch", "project"),
        new("translations.edit", "Lưu nháp và gửi duyệt", "project"),
        new("translations.assign", "Phân công dịch", "project"),
        new("translations.create_missing", "Thêm bản ghi dịch còn thiếu", "project"),
        new("translations.delete", "Xóa bản ghi dịch", "project"),
        new("comments.read", "Xem bình luận", "project"),
        new("comments.write", "Viết bình luận", "project"),
        new("history.read", "Xem lịch sử hoạt động", "project"),
        new("reviews.approve", "Xác nhận bản dịch", "project"),
        new("reviews.reject", "Từ chối bản dịch", "project"),
        new("releases.read", "Xem bản phát hành", "project"),
        new("releases.validate", "Xem điều kiện phát hành", "project"),
        new("releases.report", "Xuất báo cáo", "project"),
        new("releases.download", "Tải gói đã phát hành", "project"),
        new("releases.publish", "Chốt bản phát hành mới", "project")
    ];

    private static readonly string[] ProjectRead = ["project.read", "members.read", "locales.read", "source.read", "keys.read", "translations.read", "comments.read", "comments.write", "history.read", "releases.read", "releases.validate", "releases.report", "releases.download"];

    public static PermissionDefinition[] Permissions => [.. Definitions];
    public static string[] SystemPermissions(string role) => role == "admin"
        ? Definitions.Where(item => item.Scope == "system").Select(item => item.Code).ToArray()
        : role == "user" ? ["projects.create", "projects.read_own"] : [];

    public static string[] ProjectPermissions(string role) => role switch
    {
        "manager" => Definitions.Where(item => item.Scope == "project").Select(item => item.Code).ToArray(),
        "translator" => [.. ProjectRead, "translations.edit"],
        "reviewer" => [.. ProjectRead, "reviews.approve", "reviews.reject"],
        _ => []
    };

    public static RoleDefinition[] SystemRoles =>
    [
        new("admin", "Admin hệ thống", "system", "Quản lý tài khoản và mọi dự án qua trang Admin; quyền dịch được cấp theo dự án.", SystemPermissions("admin")),
        new("user", "Người dùng", "system", "Tạo dự án của mình; tham gia dự án theo vai trò được cấp.", SystemPermissions("user"))
    ];
    public static RoleDefinition[] ProjectRoles =>
    [
        new("manager", "Manager · Quản lý", "project", "Quản lý thành viên, nguồn, bản dịch, duyệt và phát hành trong dự án.", ProjectPermissions("manager")),
        new("translator", "Translator · Người dịch", "project", "Chỉ dịch các khóa được Manager giao cho tài khoản; không duyệt hay phát hành.", ProjectPermissions("translator")),
        new("reviewer", "Reviewer · Người duyệt", "project", "Duyệt/từ chối và bình luận; không sửa nội dung dịch hay phát hành.", ProjectPermissions("reviewer"))
    ];
}
