using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Yabt.Common;
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

    private readonly IOptionsMonitor<TemporaryContentOptions>? _temporaryOptions;

    public JsonLogicalStateManifestSerializer(IOptionsMonitor<TemporaryContentOptions> temporaryOptions)
        : this(JsonMetadataOptions.Create())
    {
        _temporaryOptions = Check.NotNull(temporaryOptions);
    }

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
        using var entryOwnership = new ManifestEntryOwnership(canonicalEntries, entries);
        var manifest = new ArchiveLogicalStateManifest
        (
            ArchiveLogicalStateManifest.ExpectedDocumentType,
            ArchiveLogicalStateManifest.ExpectedSchemaVersion,
            canonicalEntries,
            string.Empty
        );

        return entryOwnership.Complete(manifest with
        {
            ManifestHash = ComputeManifestHash(manifest, canonicalEntries),
        });
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
        using var entryOwnership = new ManifestEntryOwnership(validatedManifest.Entries, manifest.Entries);
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
        StreamedManifestDocument<ArchiveLogicalStateManifestEntry> document;
        ArchiveLogicalStateManifest manifest;
        try
        {
            document = await StreamedManifestJson.ReadAsync<ArchiveLogicalStateManifestEntry>
            (
                source,
                ["documentType", "schemaVersion", "entries", "manifestHash"],
                _strictJsonOptions,
                _temporaryOptions?.CurrentValue,
                cancellationToken
            );
            try
            {
                manifest = document.Header.RootElement.Deserialize<ArchiveLogicalStateManifest>(_strictJsonOptions) ??
                    throw new JsonException("Logical state manifest JSON did not contain a manifest object.");
                manifest = manifest with { Entries = document.Entries };
            }
            catch (Exception)
            {
                document.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException("Logical state manifest JSON could not be deserialized.", ex);
        }
        using (document)
        {

            var validated = ValidateManifest(manifest);
            document.TransferEntries();
            return validated;
        }
    }

    private ArchiveLogicalStateManifest ValidateManifest
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

        var canonicalEntries = ValidateCanonicalEntries(manifest.Entries);
        using var entryOwnership = new ManifestEntryOwnership(canonicalEntries, manifest.Entries);

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

        return entryOwnership.Complete(manifest with
        {
            Entries = canonicalEntries,
        });
    }

    private TemporaryManifestEntries<ArchiveLogicalStateManifestEntry> CreateCanonicalEntries
    (
        IEnumerable<ArchiveLogicalStateManifestEntry> entries
    ) => ManifestEntryStorage.CreateCanonical
    (
        entries,
        CreateCanonicalEntry,
        entry => entry.LogicalRelativePath,
        "Logical state manifest",
        "entry path",
        _strictJsonOptions,
        _temporaryOptions?.CurrentValue
    );

    private TemporaryManifestEntries<ArchiveLogicalStateManifestEntry> ValidateCanonicalEntries
    (
        IEnumerable<ArchiveLogicalStateManifestEntry> entries
    ) => ManifestEntryStorage.ValidateCanonical
    (
        entries,
        CreateCanonicalEntry,
        entry => entry.LogicalRelativePath,
        static (left, right) => left == right,
        "Logical state manifest",
        "entry path",
        _strictJsonOptions,
        _temporaryOptions?.CurrentValue
    );

    private static ArchiveLogicalStateManifestEntry CreateCanonicalEntry(ArchiveLogicalStateManifestEntry entry)
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

        return entry with
        {
            LogicalRelativePath = logicalRelativePath,
        };
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

    private static string ComputeManifestHash
    (
        ArchiveLogicalStateManifest manifest,
        IEnumerable<ArchiveLogicalStateManifestEntry> entries
    )
    {
        return ArchiveHash.Compute(canonicalJson =>
        {
            using var writer = new Utf8JsonWriter(canonicalJson);
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
                JsonWriterBuffering.FlushIfNeeded(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
