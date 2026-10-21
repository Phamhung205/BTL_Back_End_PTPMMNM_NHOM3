namespace TranslationManager.Api.Dtos;

public sealed record CreateProjectRequest(string Name, string Slug, string SourceLocaleCode);
public sealed record UpdateProjectRequest(string Name, string Slug);
public sealed record AddMemberRequest(long UserId, string Role);
public sealed record AttachLocaleRequest(string LocaleCode);
public sealed record UpdateLocaleRequest(string Name);
