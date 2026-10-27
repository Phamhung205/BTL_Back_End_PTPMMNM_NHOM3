
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TranslationManager.Api.Support;

public static class JsonTree
{
    // Chuyển JSON thành danh sách Translation Keys.
    public static Dictionary<string, string> Flatten(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.BadRequest(
                "JSON nguồn phải là một object."
            );
        }

        var output = new Dictionary<string, string>(
            StringComparer.Ordinal
        );

        Walk(root, "", output, 1);

        // Kiểm tra xung đột khóa cha và khóa con.
        foreach (var path in output.Keys)
        {
            for (
                int i = path.IndexOf('.');
                i >= 0;
                i = path.IndexOf('.', i + 1)
            )
            {
                string parent = path[..i];

                if (output.ContainsKey(parent))
                {
                    throw ApiException.BadRequest(
                        $"Khóa '{parent}' xung đột với '{path}'."
                    );
                }
            }
        }

        return output;
    }

    // Đọc và xử lý từng phần tử JSON.
    private static void Walk(
        JsonElement node,
        string path,
        Dictionary<string, string> output,
        int depth
    )
    {
        if (depth > 64)
        {
            throw ApiException.BadRequest(
                "JSON vượt quá độ sâu 64 cấp."
            );
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(
                StringComparer.Ordinal
            );

            if (!node.EnumerateObject().Any())
            {
                throw ApiException.BadRequest(
                    $"Object tại '{path}' không được rỗng."
                );
            }

            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw ApiException.BadRequest(
                        $"Khóa JSON bị lặp: '{property.Name}'."
                    );
                }

                // Cho phép Translation Keys có dấu chấm.
                // Ví dụ: common.nav.login

                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    throw ApiException.BadRequest(
                        "Tên khóa JSON không được rỗng."
                    );
                }

                var segments = property.Name.Split('.');

                foreach (var segment in segments)
                {
                    if (
                        string.IsNullOrWhiteSpace(segment) ||
                        segment != segment.Trim()
                    )
                    {
                        throw ApiException.BadRequest(
                            $"Tên khóa '{property.Name}' không hợp lệ."
                        );
                    }
                }

                string next = string.IsNullOrEmpty(path)
                    ? property.Name
                    : $"{path}.{property.Name}";

                if (next.Length > 500)
                {
                    throw ApiException.BadRequest(
                        "Đường dẫn khóa tối đa 500 ký tự."
                    );
                }

                Walk(property.Value, next, output, depth + 1);
            }

            return;
        }

        // Không hỗ trợ JSON Array.
        if (node.ValueKind == JsonValueKind.Array)
        {
            throw ApiException.BadRequest(
                $"Không hỗ trợ mảng JSON tại '{path}'."
            );
        }

        // Giá trị phải là chuỗi.
        if (node.ValueKind != JsonValueKind.String)
        {
            throw ApiException.BadRequest(
                $"Giá trị tại '{path}' phải là chuỗi."
            );
        }

        string value = node.GetString() ?? "";

        if (value.Length > 10000)
        {
            throw ApiException.BadRequest(
                $"Giá trị tại '{path}' tối đa 10000 ký tự."
            );
        }

        // Chặn trùng đường dẫn khi nhập JSON.
        if (!output.TryAdd(path, value))
        {
            throw ApiException.BadRequest(
                $"Translation Key bị trùng: '{path}'."
            );
        }
    }

    // Cấu hình xuất JSON.
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Chuyển Translation Keys về JSON lồng nhau.
    public static string Unflatten(
        IEnumerable<KeyValuePair<string, string>> items
    )
    {
        var root = new JsonObject();

        foreach (var (key, value) in items)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw ApiException.BadRequest(
                    "Translation Key không được rỗng."
                );
            }

            var parts = key.Split('.');

            if (parts.Any(part =>
                string.IsNullOrWhiteSpace(part) ||
                part != part.Trim()
            ))
            {
                throw ApiException.BadRequest(
                    $"Translation Key '{key}' không hợp lệ."
                );
            }

            JsonObject current = root;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                string part = parts[i];

                switch (current[part])
                {
                    case null:
                        {
                            var child = new JsonObject();
                            current[part] = child;
                            current = child;
                            break;
                        }

                    case JsonObject existing:
                        {
                            current = existing;
                            break;
                        }

                    default:
                        {
                            throw ApiException.Conflict(
                                $"Khóa '{key}' xung đột cấu trúc JSON."
                            );
                        }
                }
            }

            string finalKey = parts[^1];

            if (current.ContainsKey(finalKey))
            {
                throw ApiException.Conflict(
                    $"Khóa '{key}' bị trùng hoặc xung đột."
                );
            }

            current[finalKey] = value;
        }

        return root.ToJsonString(OutputOptions);
    }
}
