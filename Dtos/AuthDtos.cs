namespace TranslationManager.Api.Dtos;

public sealed record RegisterRequest(string Email, string DisplayName, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record AuthResponse(long UserId, string Email, string DisplayName, string Token);
