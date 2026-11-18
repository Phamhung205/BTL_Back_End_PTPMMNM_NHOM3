namespace TranslationManager.Api.Dtos;

public sealed record RegisterRequest(string Email, string DisplayName, string Password, string? Phone = null);
public sealed record LoginRequest(string Email, string Password);
public sealed record AuthResponse(long UserId, string Email, string DisplayName, string Token, string SystemRole = "user");

// Đăng nhập mạng xã hội: Google gửi ID token (credential), Facebook gửi access token.
public sealed record GoogleLoginRequest(string Credential);
public sealed record FacebookLoginRequest(string AccessToken);

// Quên mật khẩu bằng OTP gửi về số điện thoại.
public sealed record ForgotPasswordOtpRequest(string Phone);
public sealed record ResetPasswordWithOtpRequest(string Phone, string Code, string NewPassword);
public sealed record UpdatePhoneRequest(string? Phone);