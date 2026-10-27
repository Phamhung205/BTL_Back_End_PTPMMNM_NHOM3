namespace TranslationManager.Api.Dtos;

public sealed record UpdateSystemUserRequest(string? DisplayName, string? SystemRole, bool IsActive, int ExpectedVersion);
