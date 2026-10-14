using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveLayout
(
    [property: JsonRequired] string LivePrefix = "",
    [property: JsonRequired] string HistPrefix = ".yabt-hist"
)
{
    public static ArchiveLayout Default { get; } = new();

    public string ToLiveObjectKey(string relativePath)
    {
        return CombinePrefixAndRelativePath(LivePrefix, relativePath);
    }

    public string ToHistoryObjectKey(string relativePath)
    {
        return CombinePrefixAndRelativePath(HistPrefix, relativePath);
    }

    public string ToObjectKey
    (
        ArchiveArea area,
        string relativePath
    )
    {
        return area switch
        {
            ArchiveArea.Live => ToLiveObjectKey(relativePath),
            ArchiveArea.Hist => ToHistoryObjectKey(relativePath),
            _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
        };
    }

    public static string NormalizeObjectKey(string? value) => ObjectPath.Parse(value).Value;

    public static string? NormalizeObjectPrefix(string? value)
    {
        var path = ObjectPath.Parse(value);
        return path.IsRoot ? null : path.Value;
    }

    public static string CombinePrefixAndRelativePath
    (
        string? prefix,
        string? relativePath
    ) => ObjectPath.Parse(prefix).Combine(ObjectPath.Parse(relativePath)).Value;

    public static bool IsUnderPrefix
    (
        string objectKey,
        string? prefix
    ) => ObjectPath.Parse(objectKey).IsUnder(ObjectPath.Parse(prefix));

    public static string RemovePrefix
    (
        string objectKey,
        string? prefix
    )
    {
        var path = ObjectPath.Parse(objectKey);
        var prefixPath = ObjectPath.Parse(prefix);
        if (!path.IsUnder(prefixPath))
        {
            throw new ArgumentException("Object key is not under the expected prefix.", nameof(objectKey));
        }

        if (prefixPath.IsRoot) { return path.Value; }
        return path == prefixPath ? string.Empty : path.Value[(prefixPath.Value.Length + 1)..];
    }
}
