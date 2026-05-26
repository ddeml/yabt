using System.Text.Json;
using Yabt.Core.Models;

namespace Yabt.Metadata;

public sealed class JsonManifestSerializer(JsonSerializerOptions _jsonOptions) : IManifestSerializer
{
    public JsonManifestSerializer()
        : this(JsonMetadataOptions.Create())
    {
    }

    public async Task WriteAsync
    (
        ArchiveManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        await JsonSerializer.SerializeAsync(
            destination,
            manifest,
            _jsonOptions,
            cancellationToken);
    }

    public async Task<ArchiveManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        var manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(
            source,
            _jsonOptions,
            cancellationToken);

        return manifest ?? throw new InvalidDataException("Manifest JSON did not contain a manifest object.");
    }
}
