namespace Yabt.Core.Models;

public sealed record ArchiveLayout
(
    string LivePrefix = "",
    string HistPrefix = ".yabt-hist"
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

    public static string NormalizeObjectKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var segments = value
            .Replace('\\', '/')
            .Trim('/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException
                (
                    $"Object key '{value}' contains an invalid segment.",
                    nameof(value)
                );
            }
        }

        return string.Join('/', segments);
    }

    public static string? NormalizeObjectPrefix(string? value)
    {
        var normalized = NormalizeObjectKey(value);
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    public static string CombinePrefixAndRelativePath
    (
        string? prefix,
        string? relativePath
    )
    {
        var normalizedPrefix = NormalizeObjectKey(prefix);
        var normalizedRelativePath = NormalizeObjectKey(relativePath);

        if (string.IsNullOrEmpty(normalizedPrefix))
        {
            return normalizedRelativePath;
        }

        return string.IsNullOrEmpty(normalizedRelativePath) ?
            normalizedPrefix :
            $"{normalizedPrefix}/{normalizedRelativePath}";
    }

    public static bool IsUnderPrefix
    (
        string objectKey,
        string? prefix
    )
    {
        var normalizedObjectKey = NormalizeObjectKey(objectKey);
        var normalizedPrefix = NormalizeObjectKey(prefix);

        return string.IsNullOrEmpty(normalizedPrefix) ||
            string.Equals(normalizedObjectKey, normalizedPrefix, StringComparison.Ordinal) ||
            normalizedObjectKey.StartsWith($"{normalizedPrefix}/", StringComparison.Ordinal);
    }

    public static string RemovePrefix
    (
        string objectKey,
        string? prefix
    )
    {
        var normalizedObjectKey = NormalizeObjectKey(objectKey);
        var normalizedPrefix = NormalizeObjectKey(prefix);

        if (string.IsNullOrEmpty(normalizedPrefix))
        {
            return normalizedObjectKey;
        }

        if (string.Equals(normalizedObjectKey, normalizedPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var expectedPrefix = $"{normalizedPrefix}/";
        if (!normalizedObjectKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Object key is not under the expected prefix.", nameof(objectKey));
        }

        return normalizedObjectKey[expectedPrefix.Length..];
    }
}
