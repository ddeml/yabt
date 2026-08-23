using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yabt.AzureBlob;
using Yabt.AzureBlob.Implementation;
using Yabt.Core.Abstractions;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtAzureBlobServiceCollectionExtensions
{
    public static IServiceCollection AddYabtAzureBlobObjectStore
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        var effectiveConfigSectionPath = configSectionPath ??
            AzureBlobObjectStoreOptions.DefaultConfigurationSectionPath;
        if (string.IsNullOrWhiteSpace(effectiveConfigSectionPath))
        {
            throw new ArgumentException
            (
                "Azure Blob configuration section path must not be empty.",
                nameof(configSectionPath)
            );
        }

        var optionsBuilder = services.AddOptions<AzureBlobObjectStoreOptions>();
        optionsBuilder.BindConfiguration(effectiveConfigSectionPath);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.TryAddSingleton<AzureBlobContainerClientFactory>();
        services.AddSingleton<AzureBlobObjectStore>();
        services.AddSingleton<IObjectStore>(provider =>
            provider.GetRequiredService<AzureBlobObjectStore>());
        services.AddSingleton<IArchiveMutableObjectStore>(provider =>
            provider.GetRequiredService<AzureBlobObjectStore>());
        services.AddSingleton<AzureBlobBackupRootStoreResolver>();
        services.AddSingleton<IBackupRootStoreResolver>(provider =>
            provider.GetRequiredService<AzureBlobBackupRootStoreResolver>());
        return services;
    }
}
