using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text;

namespace Yabt.Core.Models;

/// <summary>
/// Creates a versioned quick-change fingerprint from file metadata.
/// This avoids reading unchanged content, but it is not proof that the bytes are identical.
/// </summary>
public static class ArchiveChangeFingerprint
{
    private const string AlgorithmName = "yabt-stat-v1-" + ArchiveHash.AlgorithmName;

    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("yabt-stat-v1");

    // This is a fast change hint derived from metadata, not proof of the object's byte content.
    public static string Create(long contentLength, DateTimeOffset lastModifiedUtc)
    {
        var hash = new XxHash128();
        hash.Append(Domain);

        Span<byte> value = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(value, contentLength);
        hash.Append(value);

        BinaryPrimitives.WriteInt64BigEndian(value, lastModifiedUtc.UtcDateTime.Ticks);
        hash.Append(value);

        return $"{AlgorithmName}:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
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
