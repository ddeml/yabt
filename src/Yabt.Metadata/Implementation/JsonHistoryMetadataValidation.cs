using System.Text.Json;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal static class JsonHistoryMetadataValidation
{
    public static ArchiveHistoryManifestEntry CreateCanonicalEntry
    (
        ArchiveHistoryManifestEntry entry,
        string documentDescription
    )
    {
        ArgumentNullException.ThrowIfNull(entry);

        var relativePath = NormalizePath(
            entry.RelativePath,
            $"{documentDescription} relative path");
        var storedRelativePath = NormalizePath(
            entry.StoredRelativePath,
            $"{documentDescription} stored relative path");

        if (!ArchiveHistoryEntryRepresentation.IsSupported(entry.Representation))
        {
            throw new YabtMetadataException(
                $"{documentDescription} has an unsupported representation.");
        }

        if (string.Equals(
                entry.Representation,
                ArchiveHistoryEntryRepresentation.Materialized,
                StringComparison.Ordinal) &&
            !string.Equals(relativePath, storedRelativePath, StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                $"{documentDescription} materialized paths must identify the same object.");
        }

        if (string.Equals(
                entry.Representation,
                ArchiveHistoryEntryRepresentation.Reference,
                StringComparison.Ordinal) &&
            !ArchiveHistoryFileNames.IsReferencePathFor(relativePath, storedRelativePath))
        {
            throw new YabtMetadataException(
                $"{documentDescription} reference stored path must preserve the original path " +
                $"and end with '{ArchiveHistoryFileNames.ReferenceSuffix}'.");
        }

        if (entry.ContentLength < 0)
        {
            throw new YabtMetadataException(
                $"{documentDescription} has a negative content length.");
        }

        if (!ArchiveHash.IsValid(entry.ContentHash))
        {
            throw new YabtMetadataException(
                $"{documentDescription} content hash is not a valid xxHash128 hash.");
        }

        if (entry.ContentType is not null && string.IsNullOrWhiteSpace(entry.ContentType))
        {
            throw new YabtMetadataException(
                $"{documentDescription} content type cannot be empty.");
        }

        var metadata = CreateCanonicalMetadata(entry.Metadata, documentDescription);
        return entry with
        {
            RelativePath = relativePath,
            StoredRelativePath = storedRelativePath,
            LastModifiedUtc = entry.LastModifiedUtc?.ToUniversalTime(),
            Metadata = metadata,
        };
    }

    public static bool EntriesAreEqual
    (
        ArchiveHistoryManifestEntry left,
        ArchiveHistoryManifestEntry right
    )
    {
        return string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) &&
            string.Equals(left.StoredRelativePath, right.StoredRelativePath, StringComparison.Ordinal) &&
            string.Equals(left.Representation, right.Representation, StringComparison.Ordinal) &&
            left.ContentLength == right.ContentLength &&
            string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal) &&
            HaveSameExactTimestamp(left.LastModifiedUtc, right.LastModifiedUtc) &&
            string.Equals(left.ContentType, right.ContentType, StringComparison.Ordinal) &&
            HaveSameMetadata(left.Metadata, right.Metadata);
    }

    public static void WriteCanonicalEntry
    (
        Utf8JsonWriter writer,
        ArchiveHistoryManifestEntry entry
    )
    {
        writer.WriteStartObject();
        writer.WriteString("relativePath", entry.RelativePath);
        writer.WriteString("storedRelativePath", entry.StoredRelativePath);
        writer.WriteString("representation", entry.Representation);
        writer.WriteNumber("contentLength", entry.ContentLength);
        writer.WriteString("contentHash", entry.ContentHash);
        if (entry.LastModifiedUtc.HasValue)
        {
            writer.WriteString("lastModifiedUtc", entry.LastModifiedUtc.Value);
        }
        if (entry.ContentType is not null)
        {
            writer.WriteString("contentType", entry.ContentType);
        }
        if (entry.Metadata is not null)
        {
            writer.WriteStartObject("metadata");
            foreach (var pair in entry.Metadata)
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static string NormalizePath(string value, string description)
    {
        string normalizedPath;
        try
        {
            normalizedPath = ArchiveLayout.NormalizeObjectKey(value);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException($"{description} is invalid.", ex);
        }

        if (string.IsNullOrEmpty(normalizedPath))
        {
            throw new YabtMetadataException($"{description} cannot be empty.");
        }

        if (string.Equals(normalizedPath, ArchiveHistoryFileNames.Manifest, StringComparison.Ordinal) ||
            string.Equals(
                normalizedPath,
                ArchiveHistoryManifest.InvalidationMarkerFileName,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                $"{description} cannot identify history-manifest control metadata.");
        }

        return normalizedPath;
    }

    private static SortedDictionary<string, string>? CreateCanonicalMetadata
    (
        IReadOnlyDictionary<string, string>? metadata,
        string documentDescription
    )
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var canonicalMetadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new YabtMetadataException(
                    $"{documentDescription} contains an empty metadata name.");
            }

            if (pair.Value is null)
            {
                throw new YabtMetadataException(
                    $"{documentDescription} metadata value '{pair.Key}' cannot be null.");
            }

            canonicalMetadata.Add(pair.Key, pair.Value);
        }

        return canonicalMetadata;
    }

    private static bool HaveSameExactTimestamp
    (
        DateTimeOffset? left,
        DateTimeOffset? right
    )
    {
        if (!left.HasValue || !right.HasValue)
        {
            return left.HasValue == right.HasValue;
        }

        return left.Value.EqualsExact(right.Value);
    }

    private static bool HaveSameMetadata
    (
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right
    )
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.SequenceEqual(right);
    }
}
