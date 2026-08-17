using System.Text.Json;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonChangeManifestSerializer(JsonSerializerOptions _jsonOptions) :
    IChangeManifestSerializer
{
    public JsonChangeManifestSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public ArchiveChangeManifest Create(IEnumerable<ArchiveChangeManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var canonicalEntries = CreateCanonicalEntries(entries);
        var manifest = new ArchiveChangeManifest
        (
            ArchiveChangeManifest.ExpectedDocumentType,
            ArchiveChangeManifest.ExpectedSchemaVersion,
            canonicalEntries,
            string.Empty
        );

        return manifest with
        {
            ManifestHash = ComputeManifestHash
            (
                manifest,
                canonicalEntries
            ),
        };
    }

    public async Task WriteAsync
    (
        ArchiveChangeManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(destination);

        var validatedManifest = ValidateManifest(manifest);
        try
        {
            await JsonSerializer.SerializeAsync(
                destination,
                validatedManifest,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException("Change manifest JSON could not be serialized.", ex);
        }
    }

    public async Task<ArchiveChangeManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        ArchiveChangeManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<ArchiveChangeManifest>(
                source,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException("Change manifest JSON could not be deserialized.", ex);
        }

        if (manifest is null)
        {
            throw new YabtMetadataException("Change manifest JSON did not contain a manifest object.");
        }

        return ValidateManifest(manifest);
    }

    private static ArchiveChangeManifest ValidateManifest(ArchiveChangeManifest manifest)
    {
        if (!string.Equals(
                manifest.DocumentType,
                ArchiveChangeManifest.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException("Change manifest JSON has an unexpected document type.");
        }

        if (manifest.SchemaVersion != ArchiveChangeManifest.ExpectedSchemaVersion)
        {
            throw new YabtMetadataException("Change manifest JSON has an unsupported schema version.");
        }

        if (manifest.Entries is null)
        {
            throw new YabtMetadataException("Change manifest JSON does not contain an entries collection.");
        }

        var serializedEntries = manifest.Entries.ToArray();
        var canonicalEntries = CreateCanonicalEntries(serializedEntries);
        EnsureEntriesAreCanonical(serializedEntries, canonicalEntries);
        ValidateManifestHash(manifest.ManifestHash);

        var expectedHash = ComputeManifestHash
        (
            manifest,
            canonicalEntries
        );
        if (!string.Equals(manifest.ManifestHash, expectedHash, StringComparison.Ordinal))
        {
            throw new YabtMetadataException("Change manifest self-hash does not match its contents.");
        }

        return manifest with
        {
            Entries = canonicalEntries,
        };
    }

    private static ArchiveChangeManifestEntry[] CreateCanonicalEntries
    (
        IEnumerable<ArchiveChangeManifestEntry> entries
    )
    {
        var canonicalEntries = new List<ArchiveChangeManifestEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new YabtMetadataException("Change manifest entries cannot contain null values.");
            }

            string relativePath;
            try
            {
                relativePath = ArchiveLayout.NormalizeObjectKey(entry.RelativePath);
            }
            catch (Exception ex)
            {
                throw new YabtMetadataException(
                    $"Change manifest entry path '{entry.RelativePath}' is invalid.",
                    ex);
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                throw new YabtMetadataException("Change manifest entry paths cannot be empty.");
            }

            if (entry.ArtifactLength < 0)
            {
                throw new YabtMetadataException(
                    $"Change manifest entry '{relativePath}' has a negative artifact length.");
            }

            ValidateFingerprint(
                entry.ChangeFingerprint,
                $"Change manifest entry '{relativePath}' change fingerprint");

            if (entry.ContentHash is not null)
            {
                ValidateContentHash(
                    entry.ContentHash,
                    $"Change manifest entry '{relativePath}' content hash");
            }

            canonicalEntries.Add(entry with
            {
                RelativePath = relativePath,
            });
        }

        canonicalEntries.Sort(static (left, right) =>
            string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal));

        for (var index = 1; index < canonicalEntries.Count; index++)
        {
            if (string.Equals(
                    canonicalEntries[index - 1].RelativePath,
                    canonicalEntries[index].RelativePath,
                    StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    $"Change manifest contains duplicate entry path '{canonicalEntries[index].RelativePath}'.");
            }
        }

        return [.. canonicalEntries];
    }

    private static void EnsureEntriesAreCanonical
    (
        ArchiveChangeManifestEntry[] serializedEntries,
        ArchiveChangeManifestEntry[] canonicalEntries
    )
    {
        for (var index = 0; index < serializedEntries.Length; index++)
        {
            var serializedEntry = serializedEntries[index];
            var canonicalEntry = canonicalEntries[index];

            if (!string.Equals(
                    serializedEntry.RelativePath,
                    canonicalEntry.RelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    serializedEntry.ChangeFingerprint,
                    canonicalEntry.ChangeFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    serializedEntry.ContentHash,
                    canonicalEntry.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    "Change manifest entries are not in canonical path order or contain noncanonical values.");
            }
        }
    }

    private static void ValidateFingerprint(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new YabtMetadataException($"{description} is required.");
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new YabtMetadataException($"{description} must include a type and value.");
        }

        var qualifier = value.AsSpan(0, separator);
        var fingerprintValue = value.AsSpan(separator + 1);
        if (!IsValidQualifier(qualifier) ||
            fingerprintValue.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            throw new YabtMetadataException($"{description} is not a valid type-qualified fingerprint.");
        }
    }

    private static bool IsValidQualifier(ReadOnlySpan<char> qualifier)
    {
        foreach (var character in qualifier)
        {
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateManifestHash(string value)
    {
        if (!ArchiveHash.IsValid(value))
        {
            throw new YabtMetadataException(
                "Change manifest self-hash is not a valid xxHash128 hash.");
        }
    }

    private static void ValidateContentHash(string value, string description)
    {
        if (!ArchiveHash.IsValid(value))
        {
            throw new YabtMetadataException(
                $"{description} is not a valid xxHash128 hash.");
        }
    }

    private static string ComputeManifestHash
    (
        ArchiveChangeManifest manifest,
        IEnumerable<ArchiveChangeManifestEntry> entries
    )
    {
        using var canonicalJson = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalJson))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", manifest.DocumentType);
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteStartArray("entries");

            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", entry.RelativePath);
                writer.WriteString("changeFingerprint", entry.ChangeFingerprint);
                if (entry.ArtifactLength.HasValue)
                {
                    writer.WriteNumber("artifactLength", entry.ArtifactLength.Value);
                }
                if (entry.ContentHash is not null)
                {
                    writer.WriteString("contentHash", entry.ContentHash);
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var canonicalBytes = canonicalJson.GetBuffer().AsSpan(0, checked((int)canonicalJson.Length));
        return ArchiveHash.Compute(canonicalBytes);
    }
}
