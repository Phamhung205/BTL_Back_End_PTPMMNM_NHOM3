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
            foreach (var property in node.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                    throw ApiException.BadRequest("Tên khóa JSON không được rỗng.");
                if (property.Name.Contains('.'))
                    throw ApiException.BadRequest($"Tên khóa '{property.Name}' không được chứa dấu chấm trong quy ước hiện tại.");
                var next = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                Walk(property.Value, next, output);
            }
            return;
        }

        if (node.ValueKind == JsonValueKind.Array)
            throw ApiException.BadRequest($"Mảng chưa được hỗ trợ tại '{path}'. BTL công bố chỉ hỗ trợ JSON object lồng nhau với giá trị chuỗi.");

        if (node.ValueKind != JsonValueKind.String)
            throw ApiException.BadRequest($"Giá trị tại '{path}' phải là chuỗi.");

        output[path] = node.GetString() ?? string.Empty;
    }

    public static string Unflatten(IEnumerable<KeyValuePair<string, string>> items)
    {
        var root = new JsonObject();
        foreach (var (key, value) in items)
        {
            var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
            JsonObject current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (current[parts[i]] is not JsonObject child)
                {
                    child = new JsonObject();
                    current[parts[i]] = child;
                }
                current = child;
            }
            current[parts[^1]] = value;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
