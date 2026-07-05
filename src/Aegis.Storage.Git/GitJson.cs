using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Storage.Git;

/// <summary>Shared serializer settings for everything stored in the repo.</summary>
internal static class GitJson
{
    /// <summary>
    /// One-document-per-file settings. Indented so commits produce small,
    /// reviewable line diffs and git can merge non-overlapping field updates.
    /// </summary>
    public static readonly JsonSerializerOptions Document = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Compact settings for append-only line-per-entry files.</summary>
    public static readonly JsonSerializerOptions Line = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
