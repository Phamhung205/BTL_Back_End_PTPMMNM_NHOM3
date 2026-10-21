namespace TranslationManager.Api.Models;

/// <summary>
/// Vòng đời bản dịch:
/// missing -> draft (lưu nháp) -> translated (gửi review) -> approved (reviewer duyệt).
/// Reviewer từ chối hoặc chuỗi nguồn thay đổi -> needs_review (người dịch cần sửa lại).
/// </summary>
public static class TranslationStatus
{
    /// <summary>Trạng thái suy ra: chưa có bản dịch (không lưu trong DB).</summary>
    public const string Missing = "missing";
    public const string Draft = "draft";
    public const string Translated = "translated";
    public const string NeedsReview = "needs_review";
    public const string Approved = "approved";

    public static readonly string[] All = [Draft, Translated, NeedsReview, Approved];
    public static readonly string[] Filterable = [Missing, Draft, Translated, NeedsReview, Approved];
}
