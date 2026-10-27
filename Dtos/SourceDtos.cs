using System.Text.Json;

namespace TranslationManager.Api.Dtos;

// RawJson preserves duplicate properties and the original file size through the PHP gateway.
// Existing clients can continue sending Data.
public sealed record ImportSourceRequest(string VersionName, JsonElement Data = default, string? RawJson = null);
