using System.Text.Json;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonHistoryManifestSerializer(JsonSerializerOptions _jsonOptions) :
    IHistoryManifestSerializer
{
    private readonly JsonSerializerOptions _strictJsonOptions = new(_jsonOptions)
    {
        AllowDuplicateProperties = false,
    };

    private readonly IOptionsMonitor<TemporaryContentOptions>? _temporaryOptions;

    public JsonHistoryManifestSerializer(IOptionsMonitor<TemporaryContentOptions> temporaryOptions)
        : this(JsonMetadataOptions.Create())
    {
        _temporaryOptions = Check.NotNull(temporaryOptions);
    }

    public JsonHistoryManifestSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public ArchiveHistoryManifest Create(IEnumerable<ArchiveHistoryManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var canonicalEntries = CreateCanonicalEntries(entries);
        using var entryOwnership = new ManifestEntryOwnership(canonicalEntries, entries);
        var manifest = new ArchiveHistoryManifest
        (
            ArchiveHistoryManifest.ExpectedDocumentType,
            ArchiveHistoryManifest.ExpectedSchemaVersion,
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
        ArchiveHistoryManifest manifest,
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
        StreamedManifestDocument<ArchiveHistoryManifestEntry> document;
        ArchiveHistoryManifest manifest;
        try
        {
            document = await StreamedManifestJson.ReadAsync<ArchiveHistoryManifestEntry>
            (
                source,
                ["documentType", "schemaVersion", "entries", "manifestHash"],
                _strictJsonOptions,
                _temporaryOptions?.CurrentValue,
                cancellationToken
            );
            try
            {
                manifest = document.Header.RootElement.Deserialize<ArchiveHistoryManifest>(_strictJsonOptions) ??
                    throw new JsonException("History manifest JSON did not contain a manifest object.");
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
            throw new YabtMetadataException("History manifest JSON could not be deserialized.", ex);
        }
        using (document)
        {

            var validated = ValidateManifest(manifest);
            document.TransferEntries();
            return validated;
        }
    }

    private ArchiveHistoryManifest ValidateManifest(ArchiveHistoryManifest manifest)
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

        var canonicalEntries = ValidateCanonicalEntries(manifest.Entries);
        using var entryOwnership = new ManifestEntryOwnership(canonicalEntries, manifest.Entries);

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

        return entryOwnership.Complete(manifest with
        {
            Entries = canonicalEntries,
        });
    }

    private TemporaryManifestEntries<ArchiveHistoryManifestEntry> CreateCanonicalEntries
    (
        IEnumerable<ArchiveHistoryManifestEntry> entries
    ) => ManifestEntryStorage.CreateCanonical
    (
        entries,
        CreateCanonicalEntry,
        entry => entry.RelativePath,
        "History manifest",
        "entry path",
        _strictJsonOptions,
        _temporaryOptions?.CurrentValue,
        entry => entry.StoredRelativePath
    );

    private TemporaryManifestEntries<ArchiveHistoryManifestEntry> ValidateCanonicalEntries
    (
        IEnumerable<ArchiveHistoryManifestEntry> entries
    ) => ManifestEntryStorage.ValidateCanonical
    (
        entries,
        CreateCanonicalEntry,
        entry => entry.RelativePath,
        JsonHistoryMetadataValidation.EntriesAreEqual,
        "History manifest",
        "entry path",
        _strictJsonOptions,
        _temporaryOptions?.CurrentValue,
        entry => entry.StoredRelativePath
    );

    private static ArchiveHistoryManifestEntry CreateCanonicalEntry(ArchiveHistoryManifestEntry entry)
    {
        if (entry is null)
        {
            throw new YabtMetadataException("History manifest entries cannot contain null values.");
        }
        return JsonHistoryMetadataValidation.CreateCanonicalEntry(entry, $"History manifest entry '{entry.RelativePath}'");
    }

    private static string ComputeManifestHash
    (
        ArchiveHistoryManifest manifest,
        IEnumerable<ArchiveHistoryManifestEntry> entries
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
                JsonHistoryMetadataValidation.WriteCanonicalEntry(writer, entry);
                JsonWriterBuffering.FlushIfNeeded(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
