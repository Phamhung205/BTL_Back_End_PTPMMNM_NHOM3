using System.Text.Json;

namespace TranslationManager.Api.Dtos;

public sealed record ImportSourceRequest(string VersionName, JsonElement Data);
