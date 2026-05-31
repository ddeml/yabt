using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Testing;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtTestingServiceCollectionExtensions
{
    public static IServiceCollection AddYabtInMemoryObjectStore
    (
        this IServiceCollection services,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => new MemoryObjectStore(
            timeProvider ?? TimeProvider.System,
            provider.GetService<ILogger<MemoryObjectStore>>() ??
                NullLogger<MemoryObjectStore>.Instance));
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
