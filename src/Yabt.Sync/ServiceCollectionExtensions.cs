using Microsoft.Extensions.DependencyInjection;

namespace Yabt.Sync;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddYabtSync(this IServiceCollection services)
    {
        services.AddSingleton<IArchiveSynchronizer, ArchiveSynchronizer>();
        return services;
    }
}
