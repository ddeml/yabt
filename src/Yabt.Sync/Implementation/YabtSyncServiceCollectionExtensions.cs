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
        if (configSectionPath is not null && string.IsNullOrWhiteSpace(configSectionPath))
        {
            throw new ArgumentException(
                "YABT synchronization configuration section path must not be empty.",
                nameof(configSectionPath));
        }

        var optionsBuilder = services.AddOptions<YabtSyncOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindConfiguration(configSectionPath);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IArchiveSynchronizer, ArchiveSynchronizer>();
        services.AddSingleton<IRestorePathVerifier, RestorePathVerifier>();
        services.AddSingleton<IHistoryDeduplicator, HistoryDeduplicator>();
        return services;
    }
}
