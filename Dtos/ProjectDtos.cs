namespace TranslationManager.Api.Dtos;

public sealed record CreateProjectRequest(string Name, string Slug, string SourceLocaleCode,
    string[]? TargetLocaleCodes = null, AddMemberRequest[]? Members = null);
public sealed record UpdateProjectRequest(string Name, string Slug);
public sealed record AddMemberRequest(long? UserId, string Role, string? Email = null);
public sealed record UpdateMemberRoleRequest(string Role);
public sealed record AttachLocaleRequest(string LocaleCode);
public sealed record UpdateLocaleRequest(string Name);
