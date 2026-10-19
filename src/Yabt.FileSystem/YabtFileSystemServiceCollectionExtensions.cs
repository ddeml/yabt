using Yabt.Core.Abstractions;
using Yabt.FileSystem;
using Yabt.FileSystem.Implementation;
using Yabt.Metadata;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtFileSystemServiceCollectionExtensions
{
    public static IServiceCollection AddYabtFileSystemObjectStore
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        var optionsBuilder = services.AddOptions<FileSystemObjectStoreOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindYabtConfiguration(configSectionPath);
        }

        services.AddSingleton<FileSystemObjectStore>();
        services.AddSingleton<IObjectStore>(provider =>
            provider.GetRequiredService<FileSystemObjectStore>());
        services.AddSingleton<IArchiveMutableObjectStore>(provider =>
            provider.GetRequiredService<FileSystemObjectStore>());
        services.AddSingleton<FileSystemBackupRootStoreResolver>();
        services.AddSingleton<IBackupRootStoreResolver>(provider =>
            provider.GetRequiredService<FileSystemBackupRootStoreResolver>());
        services.AddSingleton<ISourceRootObjectStoreResolver>(provider =>
            provider.GetRequiredService<FileSystemBackupRootStoreResolver>());
        services.AddYabtFileSystemArchiveLocations();
        return services;
    }

    public static IServiceCollection AddYabtFileSystemArchiveLocations(this IServiceCollection services)
    {
        services.AddSingleton<IArchiveLocationResolver, FileSystemArchiveLocationResolver>();
        services.AddSingleton<IRestoreDestinationFactory, FileSystemRestoreDestinationFactory>();
        return services;
    }
}
