using Azure.Core;
using Azure.Storage.Blobs;
using Yabt.Common;

namespace Yabt.AzureBlob.Implementation;

internal sealed class AzureBlobContainerClientFactory(TokenCredential credential)
{
    private readonly TokenCredential _credential = Check.NotNull(credential);

    public BlobContainerClient Create(AzureBlobObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var containerName = options.GetEffectiveContainerName();
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new YabtAzureBlobException("Azure Blob object store requires a container name.");
        }

        var serviceUri = options.ServiceUri;
        if (serviceUri is not null)
        {
            ValidateServiceUri(serviceUri);
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobContainerClient(
                options.ConnectionString,
                containerName,
                options.CreateClientOptions());
        }

        if (serviceUri is null)
        {
            throw new YabtAzureBlobException
            (
                "Azure Blob object store requires a service URI when a connection string is not configured. " +
                "A token credential supplies authentication but cannot determine the storage endpoint."
            );
        }

        return new BlobServiceClient(
            serviceUri,
            _credential,
            options.CreateClientOptions()).GetBlobContainerClient(containerName);
    }

    private static void ValidateServiceUri(Uri serviceUri)
    {
        if (!serviceUri.IsAbsoluteUri)
        {
            throw new YabtAzureBlobException("Azure Blob object store requires an absolute service URI.");
        }

        if (!string.Equals(serviceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new YabtAzureBlobException("Azure Blob object store service URI must use HTTPS.");
        }

        if (!string.IsNullOrEmpty(serviceUri.UserInfo) ||
            !string.IsNullOrEmpty(serviceUri.Query) ||
            !string.IsNullOrEmpty(serviceUri.Fragment))
        {
            throw new YabtAzureBlobException
            (
                "Azure Blob object store service URI must not contain user information, a query, or a fragment. " +
                "Provide credentials through runtime configuration instead of a SAS-bearing URI."
            );
        }
    }
}
