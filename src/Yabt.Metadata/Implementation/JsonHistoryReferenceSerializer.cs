using System.Text.Json;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonHistoryReferenceSerializer(JsonSerializerOptions _jsonOptions) :
    IHistoryReferenceSerializer
{
    public JsonHistoryReferenceSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public ArchiveHistoryContentReference Create(ArchiveHistoryManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var canonicalEntry = JsonHistoryMetadataValidation.CreateCanonicalEntry(
            entry,
            "History content reference entry");
        EnsureReferenceRepresentation(canonicalEntry);

        var reference = new ArchiveHistoryContentReference
        (
            ArchiveHistoryContentReference.ExpectedDocumentType,
            ArchiveHistoryContentReference.ExpectedSchemaVersion,
            ArchiveHistoryContentReference.DefaultMessage,
            canonicalEntry,
            string.Empty
        );

        return reference with
        {
            ReferenceHash = ComputeReferenceHash(reference, canonicalEntry),
        };
    }

    public async Task WriteAsync
    (
        ArchiveHistoryContentReference reference,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(destination);

        var validatedReference = ValidateReference(reference);
        try
        {
            await JsonSerializer.SerializeAsync(
                destination,
                validatedReference,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                "History content reference JSON could not be serialized.",
                ex);
        }
    }

    public async Task<ArchiveHistoryContentReference> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        ArchiveHistoryContentReference? reference;
        try
        {
            reference = await JsonSerializer.DeserializeAsync<ArchiveHistoryContentReference>(
                source,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                "History content reference JSON could not be deserialized.",
                ex);
        }

        if (reference is null)
        {
            throw new YabtMetadataException(
                "History content reference JSON did not contain a reference object.");
        }

        return ValidateReference(reference);
    }

    private static ArchiveHistoryContentReference ValidateReference
    (
        ArchiveHistoryContentReference reference
    )
    {
        if (!string.Equals(
                reference.DocumentType,
                ArchiveHistoryContentReference.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "History content reference JSON has an unexpected document type.");
        }

        if (reference.SchemaVersion != ArchiveHistoryContentReference.ExpectedSchemaVersion)
        {
            throw new YabtMetadataException(
                "History content reference JSON has an unsupported schema version.");
        }

        if (string.IsNullOrWhiteSpace(reference.Message))
        {
            throw new YabtMetadataException(
                "History content reference JSON does not contain a human-readable message.");
        }

        if (reference.Entry is null)
        {
            throw new YabtMetadataException(
                "History content reference JSON does not contain an entry.");
        }

        var canonicalEntry = JsonHistoryMetadataValidation.CreateCanonicalEntry(
            reference.Entry,
            "History content reference entry");
        EnsureReferenceRepresentation(canonicalEntry);
        if (!JsonHistoryMetadataValidation.EntriesAreEqual(reference.Entry, canonicalEntry))
        {
            throw new YabtMetadataException(
                "History content reference entry contains noncanonical values.");
        }

        if (!ArchiveHash.IsValid(reference.ReferenceHash))
        {
            throw new YabtMetadataException(
                "History content reference self-hash is not a valid xxHash128 hash.");
        }

        var expectedHash = ComputeReferenceHash(reference, canonicalEntry);
        if (!string.Equals(reference.ReferenceHash, expectedHash, StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "History content reference self-hash does not match its contents.");
        }

        return reference with
        {
            Entry = canonicalEntry,
        };
    }

    private static void EnsureReferenceRepresentation(ArchiveHistoryManifestEntry entry)
    {
        if (!string.Equals(
                entry.Representation,
                ArchiveHistoryEntryRepresentation.Reference,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "History content reference entry is not marked as a reference.");
        }
    }

    private static string ComputeReferenceHash
    (
        ArchiveHistoryContentReference reference,
        ArchiveHistoryManifestEntry entry
    )
    {
        using var canonicalJson = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalJson))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", reference.DocumentType);
            writer.WriteNumber("schemaVersion", reference.SchemaVersion);
            writer.WriteString("message", reference.Message);
            writer.WritePropertyName("entry");
            JsonHistoryMetadataValidation.WriteCanonicalEntry(writer, entry);
            writer.WriteEndObject();
        }

        var canonicalBytes = canonicalJson.GetBuffer().AsSpan(0, checked((int)canonicalJson.Length));
        return ArchiveHash.Compute(canonicalBytes);
    }
}
