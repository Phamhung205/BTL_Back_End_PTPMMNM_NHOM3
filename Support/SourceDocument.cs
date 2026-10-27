using System.Text;
using System.Text.Json;
using TranslationManager.Api.Dtos;

namespace TranslationManager.Api.Support;

public static class SourceDocument
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static Dictionary<string, string> Read(ImportSourceRequest request)
    {
        if (request.RawJson is not null && request.Data.ValueKind != JsonValueKind.Undefined)
            throw ApiException.BadRequest("Chỉ gửi data hoặc rawJson, không gửi cả hai.");
        var json = request.RawJson ?? (request.Data.ValueKind == JsonValueKind.Undefined ? "" : request.Data.GetRawText());
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw ApiException.BadRequest("JSON nguồn tối đa 2 MB.");
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonTree.Flatten(document.RootElement);
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest("JSON nguồn không hợp lệ hoặc vượt quá độ sâu 64 cấp.");
        }
    }
}
