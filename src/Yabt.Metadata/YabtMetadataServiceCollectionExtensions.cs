using Yabt.Metadata;
using Yabt.Metadata.Implementation;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtMetadataServiceCollectionExtensions
{
    public static IServiceCollection AddYabtMetadata
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        _ = configSectionPath;

        services.AddSingleton<IBackupRootLocator, JsonBackupRootLocator>();
        services.AddSingleton<IBackupRootReader, JsonBackupRootReader>();
        services.AddSingleton<IBackupRootSerializer, JsonBackupRootSerializer>();
        services.AddSingleton<IFolderPolicyReader, JsonFolderPolicyReader>();
        services.AddSingleton<IManifestSerializer, JsonManifestSerializer>();
        return services;
    }
}
