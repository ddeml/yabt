using Microsoft.Extensions.DependencyInjection.Extensions;
using Yabt.Sync;
using Yabt.Sync.Implementation;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130 // Namespace does not match folder structure

public static class YabtSyncServiceCollectionExtensions
{
    public static IServiceCollection AddYabtSync
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        _ = configSectionPath;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IArchiveSynchronizer, ArchiveSynchronizer>();
        return services;
    }
}
