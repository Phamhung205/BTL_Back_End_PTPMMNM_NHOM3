namespace TranslationManager.Api.Dtos;

public sealed record PublishReleaseRequest(string? VersionName, string? Note);

public sealed record ReleaseValidation(
    bool Ready,
    int ActiveStrings,
    long? LatestSourceVersionId,
    string? LatestSourceVersionName,
    LocaleProgress[] Locales,
    string[] Problems);

public sealed record ReleaseFileInfo(
    string LocaleCode,
    bool IsSource,
    string FileName,
    string Sha256,
    int KeyCount,
    long SizeBytes);

public sealed record ReleaseInfo(
    long Id,
    string Version,
    string? Note,
    int KeyCount,
    DateTime PublishedAt,
    long PublishedBy,
    string PublishedByName,
    long? SourceVersionId,
    string? SourceVersionName,
    ReleaseFileInfo[] Files);
