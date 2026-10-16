using System.Text.Json;
using MySqlConnector;

namespace TranslationManager.Api.Support;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            context.Response.StatusCode = ex.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = ex.Message,
                details = ex.Details
            });
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            context.Response.StatusCode = 409;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Dữ liệu đã tồn tại hoặc bị xung đột."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled backend error");
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Lỗi máy chủ."
            });
        }
    }
}
