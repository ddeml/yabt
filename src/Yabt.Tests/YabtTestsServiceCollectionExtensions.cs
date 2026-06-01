using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Tests;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtTestsServiceCollectionExtensions
{
    public static IServiceCollection AddYabtInMemoryObjectStore
    (
        this IServiceCollection services,
        TimeProvider? timeProvider = null,
        bool provideContentHash = default
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => new MemoryObjectStore
        (
            timeProvider ?? TimeProvider.System,
            provider.GetService<ILogger<MemoryObjectStore>>() ??
                NullLogger<MemoryObjectStore>.Instance,
            provideContentHash
        ));
        services.AddSingleton<IObjectStore>(provider => provider.GetRequiredService<MemoryObjectStore>());

        return services;
    }

    public static IServiceCollection AddYabtInMemoryObjectStore
    (
        this IServiceCollection services,
        MemoryObjectStore objectStore
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(objectStore);

        services.AddSingleton(objectStore);
        services.AddSingleton<IObjectStore>(provider => provider.GetRequiredService<MemoryObjectStore>());

        return services;
    }
}
