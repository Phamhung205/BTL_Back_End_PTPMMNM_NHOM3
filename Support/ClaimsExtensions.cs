using System.Security.Claims;

namespace TranslationManager.Api.Support;

public static class ClaimsExtensions
{
    public static long UserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw ApiException.Unauthorized();
        return long.Parse(value);
    }
}
