using Azure.Storage;
using Azure.Storage.Blobs;
using Yabt.Common;

namespace Yabt.AzureBlob.Implementation;

internal sealed class AzureBlobObjectStoreContext
(
    AzureBlobObjectStoreOptions options,
    BlobContainerClient containerClient,
    string objectStorePrefix
)
{
    public AzureBlobObjectStoreOptions Options { get; } = Check.NotNull(options);

    public BlobContainerClient ContainerClient { get; } = Check.NotNull(containerClient);

    public string ObjectStorePrefix { get; } = Check.NotNull(objectStorePrefix);

    public StorageTransferOptions UploadTransferOptions { get; } =
        Check.NotNull(options).CreateUploadTransferOptions();
}
