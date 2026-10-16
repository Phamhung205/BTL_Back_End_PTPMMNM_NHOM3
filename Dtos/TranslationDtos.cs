namespace TranslationManager.Api.Dtos;

public sealed record SaveTranslationRequest(
    long StringId,
    string LocaleCode,
    string Value,
    string Status,
    int ExpectedRowVersion);

public sealed record AssignTranslationRequest(long? UserId, int ExpectedRowVersion);
public sealed record AddCommentRequest(string Body);
public sealed record ReviewRequest(string? Note, int ExpectedRowVersion);
