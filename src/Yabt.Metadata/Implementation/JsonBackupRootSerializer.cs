using System.Text.Json;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonBackupRootSerializer(JsonSerializerOptions _jsonOptions) : IBackupRootSerializer
{
    public JsonBackupRootSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public async Task WriteAsync
    (
        BackupRootDescriptor descriptor,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        await JsonSerializer.SerializeAsync(
            destination,
            descriptor,
            _jsonOptions,
            cancellationToken);
    }

    public async Task<BackupRootDescriptor> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        BackupRootDescriptor? descriptor;

        try
        {
            descriptor = await JsonSerializer.DeserializeAsync<BackupRootDescriptor>(
                source,
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

        if (!string.Equals(
                descriptor.DocumentType,
                BackupRootDescriptor.ExpectedDocumentType,
                StringComparison.Ordinal))
        {
            throw new YabtMetadataException("Backup root JSON has an unexpected document type.");
        }

        return descriptor;
    }
}
