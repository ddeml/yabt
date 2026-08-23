using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.AzureBlob.Implementation;

internal sealed class AzureBlobBackupRootStoreResolver
(
    IConfiguration _configuration,
    ILogger<AzureBlobBackupRootStoreResolver> _logger,
    ILogger<AzureBlobObjectStore> _objectStoreLogger,
    AzureBlobContainerClientFactory _containerClientFactory,
    TimeProvider _timeProvider
) : IBackupRootStoreResolver
{
    public string StoreKind => AzureBlobObjectStoreKind.Value;

    public IObjectStore ResolveStore
    (
        BackupRootStore store,
        string descriptorRootPath
    )
    {
        _logger.LogTrace(nameof(ResolveStore));

        ArgumentNullException.ThrowIfNull(store);
        _ = descriptorRootPath;

        if (!string.Equals(store.Kind, AzureBlobObjectStoreKind.Value, StringComparison.Ordinal))
        {
            throw new YabtAzureBlobException(
                $"Store '{store.Id}' is not an Azure Blob object store.");
        }

        RejectProviderProperties(store);

        if (store.CredentialRef is not null)
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object store '{store.Id}' must configure credentials through " +
                "its runtime configuration section instead of credentialRef.");
        }

        if (store.ConfigSectionPath is not null &&
            string.IsNullOrWhiteSpace(store.ConfigSectionPath))
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object store '{store.Id}' has an empty configSectionPath.");
        }

        var configSectionPath = store.ConfigSectionPath ??
            AzureBlobObjectStoreOptions.DefaultConfigurationSectionPath;
        var section = _configuration.GetSection(configSectionPath);
        if (section.Value is null && !section.GetChildren().Any())
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object store '{store.Id}' requires configuration section " +
                $"'{configSectionPath}'.");
        }

        var options = section.Get<AzureBlobObjectStoreOptions>() ??
            throw new YabtAzureBlobException(
                $"Azure Blob object store '{store.Id}' configuration section " +
                $"'{configSectionPath}' could not be bound.");

        return new AzureBlobObjectStore
        (
            new AzureBlobObjectStoreOptionsSnapshot(options),
            _containerClientFactory,
            _objectStoreLogger,
            _timeProvider
        );
    }

    private static void RejectProviderProperties(BackupRootStore store)
    {
        if (store.ProviderProperties is not { Count: > 0 })
        {
            return;
        }

        var providerPropertyName = store.ProviderProperties.Keys.First();
        throw new YabtAzureBlobException(
            $"Azure Blob object store '{store.Id}' has unsupported provider property " +
            $"'{providerPropertyName}'. Configure every Azure Blob setting in its runtime " +
            "configuration section instead of .yabt-root.json.");
    }
}
