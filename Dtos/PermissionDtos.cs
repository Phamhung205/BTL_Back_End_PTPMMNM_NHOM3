namespace TranslationManager.Api.Dtos;

public sealed record ChangeSystemRoleRequest(string? Role, int ExpectedVersion);
public sealed record ChangeUserStatusRequest(bool? IsActive, int ExpectedVersion);
public sealed record CreateSystemUserRequest(string? Email, string? DisplayName, string? Password, string? SystemRole = "user");
public sealed record SystemUserDetails(long Id, string Email, string DisplayName, string SystemRole, bool IsActive, int SecurityVersion, DateTime CreatedAt, long ProjectCount);
public sealed record ProjectPermissionGrant(long ProjectId, string Name, string Slug, string Role, string[] Permissions, string TranslationEditScope);
public sealed record UserPermissionSnapshot(long UserId, string Email, string DisplayName, string SystemRole, bool IsActive, int SecurityVersion, string[] Permissions, ProjectPermissionGrant[] Projects);
