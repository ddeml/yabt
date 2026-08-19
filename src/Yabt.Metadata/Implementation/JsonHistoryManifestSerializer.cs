using System.Text.Json;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonHistoryManifestSerializer(JsonSerializerOptions _jsonOptions) :
    IHistoryManifestSerializer
{
    public JsonHistoryManifestSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public ArchiveHistoryManifest Create(IEnumerable<ArchiveHistoryManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var canonicalEntries = CreateCanonicalEntries(entries);
        var manifest = new ArchiveHistoryManifest
        (
            ArchiveHistoryManifest.ExpectedDocumentType,
            ArchiveHistoryManifest.ExpectedSchemaVersion,
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
        ArchiveHistoryManifest manifest,
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
            throw new YabtMetadataException("History manifest JSON could not be serialized.", ex);
        }
    }

    public async Task<ArchiveHistoryManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        ArchiveHistoryManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<ArchiveHistoryManifest>(
                source,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException("History manifest JSON could not be deserialized.", ex);
        }

        if (manifest is null)
        {
            throw new YabtMetadataException("History manifest JSON did not contain a manifest object.");
        }

        return ValidateManifest(manifest);
    }

    private static ArchiveHistoryManifest ValidateManifest(ArchiveHistoryManifest manifest)
    {
        if (!string.Equals(
                manifest.DocumentType,
                ArchiveHistoryManifest.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException("History manifest JSON has an unexpected document type.");
        }

        if (manifest.SchemaVersion != ArchiveHistoryManifest.ExpectedSchemaVersion)
        {
            throw new YabtMetadataException("History manifest JSON has an unsupported schema version.");
        }

        if (manifest.Entries is null)
        {
            throw new YabtMetadataException("History manifest JSON does not contain an entries collection.");
        }

        var serializedEntries = manifest.Entries.ToArray();
        var canonicalEntries = CreateCanonicalEntries(serializedEntries);
        EnsureEntriesAreCanonical(serializedEntries, canonicalEntries);

        if (!ArchiveHash.IsValid(manifest.ManifestHash))
        {
            throw new YabtMetadataException(
                "History manifest self-hash is not a valid xxHash128 hash.");
        }

        var expectedHash = ComputeManifestHash(manifest, canonicalEntries);
        if (!string.Equals(manifest.ManifestHash, expectedHash, StringComparison.Ordinal))
        {
            throw new YabtMetadataException("History manifest self-hash does not match its contents.");
        }

        return manifest with
        {
            Entries = canonicalEntries,
        };
    }

    private static ArchiveHistoryManifestEntry[] CreateCanonicalEntries
    (
        IEnumerable<ArchiveHistoryManifestEntry> entries
    )
    {
        var canonicalEntries = new List<ArchiveHistoryManifestEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new YabtMetadataException(
                    "History manifest entries cannot contain null values.");
            }

            canonicalEntries.Add(JsonHistoryMetadataValidation.CreateCanonicalEntry(
                entry,
                $"History manifest entry '{entry.RelativePath}'"));
        }

        canonicalEntries.Sort(static (left, right) =>
            string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal));

        var storedPaths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < canonicalEntries.Count; index++)
        {
            var entry = canonicalEntries[index];
            if (index > 0 && string.Equals(
                    canonicalEntries[index - 1].RelativePath,
                    entry.RelativePath,
                    StringComparison.Ordinal))
            {
                throw new YabtMetadataException(
                    $"History manifest contains duplicate entry path '{entry.RelativePath}'.");
            }

            if (!storedPaths.Add(entry.StoredRelativePath))
            {
                throw new YabtMetadataException(
                    $"History manifest contains duplicate stored path '{entry.StoredRelativePath}'.");
            }
        }

        return [.. canonicalEntries];
    }

    private static void EnsureEntriesAreCanonical
    (
        ArchiveHistoryManifestEntry[] serializedEntries,
        ArchiveHistoryManifestEntry[] canonicalEntries
    )
    {
        for (var index = 0; index < serializedEntries.Length; index++)
        {
            if (!JsonHistoryMetadataValidation.EntriesAreEqual(
                    serializedEntries[index],
                    canonicalEntries[index]))
            {
                throw new YabtMetadataException(
                    "History manifest entries are not in canonical path order or contain " +
                    "noncanonical values.");
            }
        }
    }

    private static string ComputeManifestHash
    (
        ArchiveHistoryManifest manifest,
        IEnumerable<ArchiveHistoryManifestEntry> entries
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
                JsonHistoryMetadataValidation.WriteCanonicalEntry(writer, entry);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var canonicalBytes = canonicalJson.GetBuffer().AsSpan(0, checked((int)canonicalJson.Length));
        return ArchiveHash.Compute(canonicalBytes);
    }
}
