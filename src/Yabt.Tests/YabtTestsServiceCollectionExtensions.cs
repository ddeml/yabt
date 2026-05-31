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
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => new InMemoryObjectStore(
            timeProvider ?? TimeProvider.System,
            provider.GetService<ILogger<InMemoryObjectStore>>() ??
                NullLogger<InMemoryObjectStore>.Instance));
        services.AddSingleton<IObjectStore>(provider => provider.GetRequiredService<InMemoryObjectStore>());

        return services;
    }

    public static IServiceCollection AddYabtInMemoryObjectStore
    (
        this IServiceCollection services,
        InMemoryObjectStore objectStore
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(objectStore);

        services.AddSingleton(objectStore);
        services.AddSingleton<IObjectStore>(provider => provider.GetRequiredService<InMemoryObjectStore>());

        return services;
    }
}
