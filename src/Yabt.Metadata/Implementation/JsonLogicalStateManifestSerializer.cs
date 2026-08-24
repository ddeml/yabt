using System.Text.Json;
using System.Text.Json.Serialization;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonLogicalStateManifestSerializer(JsonSerializerOptions _jsonOptions) :
    ILogicalStateManifestSerializer
{
    private readonly JsonSerializerOptions _strictJsonOptions = new(_jsonOptions)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public JsonLogicalStateManifestSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public ArchiveLogicalStateManifest Create
    (
        IEnumerable<ArchiveLogicalStateManifestEntry> entries
    )
    {
        ArgumentNullException.ThrowIfNull(entries);

        var canonicalEntries = CreateCanonicalEntries(entries);
        var manifest = new ArchiveLogicalStateManifest
        (
            ArchiveLogicalStateManifest.ExpectedDocumentType,
            ArchiveLogicalStateManifest.ExpectedSchemaVersion,
            canonicalEntries,
            string.Empty
        );

        return manifest with
        {
            ManifestHash = ComputeManifestHash(manifest, canonicalEntries),
        };
    }

    public async Task WriteAsync
    (
        ArchiveLogicalStateManifest manifest,
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
                _strictJsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                "Logical state manifest JSON could not be serialized.",
                ex);
        }
    }

    public async Task<ArchiveLogicalStateManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        ArchiveLogicalStateManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<ArchiveLogicalStateManifest>(
                source,
                _strictJsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                "Logical state manifest JSON could not be deserialized.",
                ex);
        }

        if (manifest is null)
        {
            throw new YabtMetadataException(
                "Logical state manifest JSON did not contain a manifest object.");
        }

        return ValidateManifest(manifest);
    }

    private static ArchiveLogicalStateManifest ValidateManifest
    (
        ArchiveLogicalStateManifest manifest
    )
    {
        if (!string.Equals(
                manifest.DocumentType,
                ArchiveLogicalStateManifest.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "Logical state manifest JSON has an unexpected document type.");
        }

        if (manifest.SchemaVersion != ArchiveLogicalStateManifest.ExpectedSchemaVersion)
        {
            throw new YabtMetadataException(
                "Logical state manifest JSON has an unsupported schema version.");
        }

        if (manifest.Entries is null)
        {
            throw new YabtMetadataException(
                "Logical state manifest JSON does not contain an entries collection.");
        }

        var serializedEntries = manifest.Entries.ToArray();
        var canonicalEntries = CreateCanonicalEntries(serializedEntries);
        EnsureEntriesAreCanonical(serializedEntries, canonicalEntries);

        if (!ArchiveHash.IsValid(manifest.ManifestHash))
        {
            throw new YabtMetadataException(
                "Logical state manifest self-hash is not a valid xxHash128 hash.");
        }

        var expectedHash = ComputeManifestHash(manifest, canonicalEntries);
        if (!string.Equals(manifest.ManifestHash, expectedHash, StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "Logical state manifest self-hash does not match its contents.");
        }

        return manifest with
        {
            Entries = canonicalEntries,
        };
    }

    private static ArchiveLogicalStateManifestEntry[] CreateCanonicalEntries
    (
        IEnumerable<ArchiveLogicalStateManifestEntry> entries
    )
    {
        var canonicalEntries = new List<ArchiveLogicalStateManifestEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new YabtMetadataException(
                    "Logical state manifest entries cannot contain null values.");
            }

            string logicalRelativePath;
            try
            {
                logicalRelativePath = ArchiveLayout.NormalizeObjectKey(
                    entry.LogicalRelativePath);
            }
            catch (Exception ex)
            {
                throw new YabtMetadataException(
                    $"Logical state manifest entry path '{entry.LogicalRelativePath}' is invalid.",
                    ex);
            }

            if (string.IsNullOrEmpty(logicalRelativePath))
            {
                throw new YabtMetadataException(
                    "Logical state manifest entry paths cannot be empty.");
            }

            ValidateStatFingerprint(entry.StatFingerprint, logicalRelativePath);
            if (!ArchiveHash.IsValid(entry.ContentHash))
            {
                throw new YabtMetadataException(
                    $"Logical state manifest entry '{logicalRelativePath}' content hash " +
                    "is not a valid xxHash128 hash.");
            }

            canonicalEntries.Add(entry with
            {
                LogicalRelativePath = logicalRelativePath,
            });
        }

        canonicalEntries.Sort(static (left, right) =>
            string.Compare(
                left.LogicalRelativePath,
                right.LogicalRelativePath,
                StringComparison.Ordinal));

        for (var index = 1; index < canonicalEntries.Count; index++)
        {
            if (string.Equals(
                    canonicalEntries[index - 1].LogicalRelativePath,
                    canonicalEntries[index].LogicalRelativePath,
                    StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    "Logical state manifest contains duplicate entry path " +
                    $"'{canonicalEntries[index].LogicalRelativePath}'.");
            }
        }

        return [.. canonicalEntries];
    }

    private static void ValidateStatFingerprint
    (
        string value,
        string logicalRelativePath
    )
    {
        if (!ArchiveChangeFingerprint.TryParse(
                value,
                out var contentLength,
                out var lastModifiedUtc) ||
            !string.Equals(
                value,
                ArchiveChangeFingerprint.Create(contentLength, lastModifiedUtc),
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                $"Logical state manifest entry '{logicalRelativePath}' stat fingerprint " +
                "is not a canonical stat-v1 fingerprint.");
        }
    }

    private static void EnsureEntriesAreCanonical
    (
        ArchiveLogicalStateManifestEntry[] serializedEntries,
        ArchiveLogicalStateManifestEntry[] canonicalEntries
    )
    {
        for (var index = 0; index < serializedEntries.Length; index++)
        {
            var serializedEntry = serializedEntries[index];
            var canonicalEntry = canonicalEntries[index];

            if (!string.Equals(
                    serializedEntry.LogicalRelativePath,
                    canonicalEntry.LogicalRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    serializedEntry.StatFingerprint,
                    canonicalEntry.StatFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    serializedEntry.ContentHash,
                    canonicalEntry.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    "Logical state manifest entries are not in canonical path order or " +
                    "contain noncanonical values.");
            }
        }
    }

    private static string ComputeManifestHash
    (
        ArchiveLogicalStateManifest manifest,
        IEnumerable<ArchiveLogicalStateManifestEntry> entries
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
                writer.WriteString("logicalRelativePath", entry.LogicalRelativePath);
                writer.WriteString("statFingerprint", entry.StatFingerprint);
                writer.WriteString("contentHash", entry.ContentHash);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var canonicalBytes = canonicalJson.GetBuffer().AsSpan(
            0,
            checked((int)canonicalJson.Length));
        return ArchiveHash.Compute(canonicalBytes);
    }
}
