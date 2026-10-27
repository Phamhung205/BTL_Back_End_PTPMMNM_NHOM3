namespace TranslationManager.Api.Dtos;

/// <param name="Status">"draft" = lưu nháp, "translated" = gửi review.</param>
/// <param name="ExpectedRowVersion">rowVersion lúc tải bản dịch; 0 nếu chưa có bản dịch. Sai lệch => HTTP 409.</param>
public sealed record SaveTranslationRequest(
    long StringId,
    string? LocaleCode,
    string? Value,
    string? Status,
    int ExpectedRowVersion);

public sealed record AssignTranslationRequest(long? UserId, int ExpectedRowVersion);

/// <summary>Phân công nhiều khóa cùng lúc, kể cả khóa chưa có bản dịch.</summary>
public sealed record BulkAssignRequest(string? LocaleCode, long[]? StringIds, long? UserId);

public sealed record CreateMissingTranslationsRequest(string? LocaleCode);
public sealed record DeleteTranslationRequest(int ExpectedRowVersion);
public sealed record TranslationDeleteItem(long TranslationId, int ExpectedRowVersion);
public sealed record BulkDeleteTranslationsRequest(string? LocaleCode, TranslationDeleteItem[]? Items);

public sealed record AddCommentRequest(string? Body);
public sealed record ReviewRequest(string? Note, int ExpectedRowVersion);

public sealed record LocaleProgress(
    string LocaleCode,
    string LocaleName,
    int Total,
    int Missing,
    int Draft,
    int Translated,
    int NeedsReview,
    int Approved);
