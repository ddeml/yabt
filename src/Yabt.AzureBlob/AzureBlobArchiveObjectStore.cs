using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.AzureBlob;

public sealed class AzureBlobArchiveObjectStore(IOptionsMonitor<AzureBlobArchiveOptions> _options) : IArchiveObjectStore
{
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await GetContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task UploadAsync
    (
        ArchiveObjectKey key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        var blobClient = GetContainerClient().GetBlobClient(key.ToBlobName());
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
        };

        await blobClient.UploadAsync(content, options, cancellationToken);
    }

    public async Task<bool> ExistsAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        var blobClient = GetContainerClient().GetBlobClient(key.ToBlobName());
        var response = await blobClient.ExistsAsync(cancellationToken);
        return response.Value;
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        ArchiveArea area,
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var containerClient = GetContainerClient();
        var normalizedPrefix = BuildPrefix(area, prefix);

        await foreach (var blob in containerClient.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: normalizedPrefix,
            cancellationToken: cancellationToken))
        {
            yield return new(
                FromBlobName(blob.Name),
                blob.Properties.ContentLength,
                blob.Properties.LastModified,
                blob.Properties.ContentHash is null ?
                    null :
                    Convert.ToHexString(blob.Properties.ContentHash).ToLowerInvariant());
        }
    }

    public async Task MoveAsync
    (
        ArchiveObjectKey source,
        ArchiveObjectKey destination,
        CancellationToken cancellationToken = default
    )
    {
        var containerClient = GetContainerClient();
        var sourceClient = containerClient.GetBlobClient(source.ToBlobName());
        var destinationClient = containerClient.GetBlobClient(destination.ToBlobName());

        var operation = await destinationClient.StartCopyFromUriAsync(
            sourceClient.Uri,
            cancellationToken: cancellationToken);

        await operation.WaitForCompletionAsync(cancellationToken);
        await sourceClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    private BlobContainerClient GetContainerClient()
    {
        var options = _options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new(
                options.ConnectionString,
                options.GetEffectiveContainerName());
        }

        if (options.ServiceUri is not null)
        {
            var serviceClient = new BlobServiceClient(options.ServiceUri);
            return serviceClient.GetBlobContainerClient(options.GetEffectiveContainerName());
        }

        throw new InvalidOperationException("Azure Blob archive store requires a connection string or service URI.");
    }

    private static string BuildPrefix(ArchiveArea area, string? prefix)
    {
        var areaPrefix = area == ArchiveArea.Live ? "live" : "hist";
        var normalized = prefix?.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalized)
            ? $"{areaPrefix}/"
            : $"{areaPrefix}/{normalized}/";
    }

    private static ArchiveObjectKey FromBlobName(string blobName)
    {
        var normalized = blobName.Replace('\\', '/').Trim('/');
        var separator = normalized.IndexOf('/', StringComparison.Ordinal);
        if (separator < 0)
        {
            return new(ArchiveArea.Live, normalized);
        }

        var area = normalized[..separator] == "hist"
            ? ArchiveArea.Hist
            : ArchiveArea.Live;

        return new(area, normalized[(separator + 1)..]);
    }
}
