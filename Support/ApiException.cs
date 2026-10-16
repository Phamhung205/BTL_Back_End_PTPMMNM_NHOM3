namespace TranslationManager.Api.Support;

public sealed class ApiException(int statusCode, string message, object? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public object? Details { get; } = details;

    public static ApiException BadRequest(string message, object? details = null) => new(400, message, details);
    public static ApiException Unauthorized(string message = "Unauthorized") => new(401, message);
    public static ApiException Forbidden(string message = "Forbidden") => new(403, message);
    public static ApiException NotFound(string message = "Not found") => new(404, message);
    public static ApiException Conflict(string message, object? details = null) => new(409, message, details);
}
