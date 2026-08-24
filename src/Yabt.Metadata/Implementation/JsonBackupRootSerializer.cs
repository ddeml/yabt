using System.Text.Json;
using System.Text.Json.Serialization;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonBackupRootSerializer : IBackupRootSerializer
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];

    private readonly JsonSerializerOptions _jsonOptions;

    public JsonBackupRootSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public JsonBackupRootSerializer(JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(jsonOptions);

        _jsonOptions = new(jsonOptions)
        {
            AllowDuplicateProperties = false,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
    }

    public async Task WriteAsync
    (
        BackupRootDescriptor descriptor,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(destination);

        var validatedDescriptor = ValidateDescriptor(descriptor);
        await JsonSerializer.SerializeAsync(
            destination,
            validatedDescriptor,
            _jsonOptions,
            cancellationToken);
    }

    public async Task<BackupRootDescriptor> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        var document = await ReadDocumentAsync(source, cancellationToken);
        return document.Descriptor;
    }

    public async Task<BackupRootDocument> ReadDocumentAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        byte[] content;
        BackupRootDescriptor? descriptor;

        try
        {
            using var contentStream = new MemoryStream();
            await source.CopyToAsync(contentStream, cancellationToken);
            content = contentStream.ToArray();

            await using var parseStream = new MemoryStream(content, writable: false);
            descriptor = await JsonSerializer.DeserializeAsync<BackupRootDescriptor>(
                parseStream,
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException("Backup root JSON could not be deserialized.", ex);
        }

        if (descriptor is null)
        {
            throw new YabtMetadataException("Backup root JSON did not contain a descriptor object.");
        }

        var documentContent = content.AsMemory();
        if (content.AsSpan().StartsWith(Utf8Preamble))
        {
            documentContent = documentContent[Utf8Preamble.Length..];
        }

        using (var document = JsonDocument.Parse(documentContent))
        {
            ValidateProviderPropertyPresence(document.RootElement);
        }

        var validatedDescriptor = ValidateDescriptor(descriptor);
        return new BackupRootDocument(validatedDescriptor, content);
    }

    private static BackupRootDescriptor ValidateDescriptor(BackupRootDescriptor descriptor)
    {
        if (!string.Equals(
                descriptor.DocumentType,
                BackupRootDescriptor.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException("Backup root JSON has an unexpected document type.");
        }

        if (descriptor.SchemaVersion != BackupRootDescriptor.ExpectedSchemaVersion)
        {
            throw new YabtMetadataException("Backup root JSON has an unsupported schema version.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.ArchiveId) ||
            !string.Equals(
                descriptor.ArchiveId,
                descriptor.ArchiveId.Trim(),
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "Backup root JSON archive id is required and must not have surrounding whitespace.");
        }

        if (descriptor.Layout is null)
        {
            throw new YabtMetadataException("Backup root JSON layout is required.");
        }

        if (descriptor.Layout.LivePrefix is null)
        {
            throw new YabtMetadataException(
                "Backup root JSON live prefix is required and cannot be null.");
        }

        try
        {
            _ = ArchiveLayout.NormalizeObjectKey(descriptor.Layout.LivePrefix);
            if (ArchiveLayout.NormalizeObjectPrefix(descriptor.Layout.HistPrefix) is null)
            {
                throw new YabtMetadataException(
                    "Backup root JSON history prefix is required and must be nonempty.");
            }
        }
        catch (YabtMetadataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new YabtMetadataException(
                "Backup root JSON layout contains an invalid object prefix.",
                ex);
        }

        if (descriptor.RootRole is not null &&
            !string.Equals(descriptor.RootRole, "source", StringComparison.Ordinal) &&
            !string.Equals(descriptor.RootRole, "target", StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                "Backup root JSON root role must be either 'source' or 'target'.");
        }

        if (descriptor.Stores is null)
        {
            throw new YabtMetadataException("Backup root JSON stores collection is required.");
        }

        var stores = descriptor.Stores.ToArray();
        if (stores.Length == 0)
        {
            throw new YabtMetadataException(
                "Backup root JSON must define at least one object store.");
        }

        var storeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in stores)
        {
            if (store is null)
            {
                throw new YabtMetadataException(
                    "Backup root JSON stores collection cannot contain null values.");
            }

            ValidateStoreToken(store.Id, "id");
            ValidateStoreToken(store.Kind, "kind");
            if (!storeIds.Add(store.Id))
            {
                throw new YabtMetadataException(
                    $"Backup root JSON contains duplicate object store id '{store.Id}'.");
            }

            if (store.ConfigSectionPath is not null &&
                string.IsNullOrWhiteSpace(store.ConfigSectionPath))
            {
                throw new YabtMetadataException(
                    $"Backup root JSON object store '{store.Id}' has an empty configuration section path.");
            }

            ValidateProviderShape(store);
        }

        if (descriptor.DefaultStoreId is not null &&
            (!storeIds.Contains(descriptor.DefaultStoreId) ||
                string.IsNullOrWhiteSpace(descriptor.DefaultStoreId)))
        {
            throw new YabtMetadataException(
                $"Backup root JSON default store id '{descriptor.DefaultStoreId}' does not identify " +
                    "a configured object store.");
        }

        var changeManifestCompression = ArchiveChangeManifestCompression.GetEffective(
            descriptor.ChangeManifestCompression);
        if (!ArchiveChangeManifestCompression.IsSupported(changeManifestCompression))
        {
            throw new YabtMetadataException(
                "Backup root JSON has an unsupported change manifest compression value.");
        }

        var tinyFileMaximumBytes = ArchiveHistoryDeduplication.GetEffectiveTinyFileMaximumBytes(
            descriptor.HistoryDeduplicationTinyFileMaximumBytes);
        if (!ArchiveHistoryDeduplication.IsSupportedTinyFileMaximumBytes(tinyFileMaximumBytes))
        {
            throw new YabtMetadataException(
                "Backup root JSON has a negative history deduplication tiny-file maximum size.");
        }

        return descriptor with
        {
            Stores = stores,
        };
    }

    private static void ValidateStoreToken(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new YabtMetadataException(
                $"Backup root JSON object store {description} is required and must not have " +
                    "surrounding whitespace.");
        }
    }

    private static void ValidateProviderShape(BackupRootStore store)
    {
        if (string.Equals(store.Kind, "azureBlob", StringComparison.Ordinal))
        {
            if (store.CredentialRef is not null ||
                store.ProviderProperties is { Count: > 0 })
            {
                throw new YabtMetadataException(
                    $"Backup root JSON Azure Blob store '{store.Id}' may contain only id, kind, " +
                        "and configSectionPath. Credentials and provider settings belong in " +
                        "runtime configuration.");
            }

            return;
        }

        if (store.ConfigSectionPath is not null)
        {
            throw new YabtMetadataException(
                $"Backup root JSON object store '{store.Id}' uses configSectionPath, which is " +
                    "supported only for Azure Blob stores.");
        }
    }

    private static void ValidateProviderPropertyPresence(JsonElement root)
    {
        if (!root.TryGetProperty("stores", out var stores) ||
            stores.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var store in stores.EnumerateArray())
        {
            if (store.ValueKind != JsonValueKind.Object ||
                !store.TryGetProperty("kind", out var kindProperty) ||
                kindProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var kind = kindProperty.GetString();
            var storeId = store.TryGetProperty("id", out var idProperty) &&
                idProperty.ValueKind == JsonValueKind.String ?
                    idProperty.GetString() ?? "<unknown>" :
                    "<unknown>";
            if (string.Equals(kind, "azureBlob", StringComparison.Ordinal))
            {
                foreach (var property in store.EnumerateObject())
                {
                    if (property.Name is "id" or "kind" or "configSectionPath")
                    {
                        continue;
                    }

                    throw new YabtMetadataException(
                        $"Backup root JSON Azure Blob store '{storeId}' may contain only id, " +
                            $"kind, and configSectionPath; property '{property.Name}' is forbidden " +
                            "even when its value is null.");
                }

                continue;
            }

            if (store.TryGetProperty("configSectionPath", out _))
            {
                throw new YabtMetadataException(
                    $"Backup root JSON non-Azure store '{storeId}' must not contain " +
                        "configSectionPath, even when its value is null.");
            }
        }
    }
}
