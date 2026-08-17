using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Yabt.Core.Models;

/// <summary>
/// Creates a versioned quick-change fingerprint from file metadata.
/// This avoids reading unchanged content, but it is not proof that the bytes are identical.
/// </summary>
public static class ArchiveChangeFingerprint
{
    private const string FormatName = "stat-v1";

    // This is a fast change hint derived from metadata, not proof of the object's byte content.
    public static string Create(long contentLength, DateTimeOffset lastModifiedUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);

        return string.Create
        (
            CultureInfo.InvariantCulture,
            $"{FormatName}:{lastModifiedUtc.UtcDateTime:O}:{contentLength}"
        );
    }

    public static bool TryCreate
    (
        long? contentLength,
        DateTimeOffset? lastModifiedUtc,
        [NotNullWhen(true)] out string? changeFingerprint
    )
    {
        // Missing metadata cannot safely identify an unchanged file. The caller must fall back to
        // a provider content hash or a complete stream comparison instead of inventing a value.
        if (!contentLength.HasValue || !lastModifiedUtc.HasValue)
        {
            changeFingerprint = null;
            return false;
        }

        changeFingerprint = Create(
            contentLength.Value,
            lastModifiedUtc.Value);
        return true;
    }
}
