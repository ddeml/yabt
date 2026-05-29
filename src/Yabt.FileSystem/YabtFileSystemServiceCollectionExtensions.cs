using Yabt.Core.Abstractions;
using Yabt.FileSystem;
using Yabt.FileSystem.Implementation;

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
        var optionsBuilder = services.AddOptions<FileSystemArchiveOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindConfiguration(configSectionPath);
        }

        services.AddSingleton<IArchiveObjectStore, FileSystemArchiveObjectStore>();
        return services;
    }
}
