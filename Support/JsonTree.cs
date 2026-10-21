using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TranslationManager.Api.Support;

public static class JsonTree
{
    public static Dictionary<string, string> Flatten(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw ApiException.BadRequest("JSON nguồn phải có object ở cấp gốc.");

        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, "", output);
        return output;
    }

    private static void Walk(JsonElement node, string path, Dictionary<string, string> output)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!node.EnumerateObject().Any()) throw ApiException.BadRequest($"Object tại '{path}' không chứa khóa dịch.");
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw ApiException.BadRequest($"Khóa JSON bị lặp tại '{path}': '{property.Name}'.");
                if (string.IsNullOrWhiteSpace(property.Name))
                    throw ApiException.BadRequest("Tên khóa JSON không được rỗng.");
                if (property.Name != property.Name.Trim()) throw ApiException.BadRequest("Tên khóa không được có khoảng trắng ở đầu hoặc cuối.");
                if (property.Name.Contains('.'))
                    throw ApiException.BadRequest($"Tên khóa '{property.Name}' không được chứa dấu chấm trong quy ước hiện tại.");
                var next = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                if (next.Length > 500) throw ApiException.BadRequest("Đường dẫn khóa JSON tối đa 500 ký tự.");
                Walk(property.Value, next, output);
            }
            return;
        }

        if (node.ValueKind == JsonValueKind.Array)
            throw ApiException.BadRequest($"Mảng chưa được hỗ trợ tại '{path}'. BTL công bố chỉ hỗ trợ JSON object lồng nhau với giá trị chuỗi.");

        if (node.ValueKind != JsonValueKind.String)
            throw ApiException.BadRequest($"Giá trị tại '{path}' phải là chuỗi.");

        var value = node.GetString() ?? string.Empty;
        if (value.Length > 10000) throw ApiException.BadRequest($"Nội dung tại '{path}' tối đa 10000 ký tự.");
        output.Add(path, value);
    }

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        // Giữ nguyên tiếng Việt/Unicode trong file phát hành thay vì \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Unflatten(IEnumerable<KeyValuePair<string, string>> items)
    {
        var root = new JsonObject();
        foreach (var (key, value) in items)
        {
            var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            JsonObject current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (current[parts[i]])
                {
                    case null:
                        var child = new JsonObject();
                        current[parts[i]] = child;
                        current = child;
                        break;
                    case JsonObject existing:
                        current = existing;
                        break;
                    default:
                        throw ApiException.Conflict($"Khóa '{key}' xung đột với một khóa lá khác khi dựng JSON.");
                }
            }

            if (current[parts[^1]] is JsonObject)
                throw ApiException.Conflict($"Khóa '{key}' xung đột với một nhóm khóa khác khi dựng JSON.");
            current[parts[^1]] = value;
        }

        return root.ToJsonString(OutputOptions);
    }
}
