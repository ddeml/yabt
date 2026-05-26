using Microsoft.Extensions.DependencyInjection;

namespace Yabt.Metadata;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddYabtMetadata(this IServiceCollection services)
    {
        services.AddSingleton<IFolderPolicyReader, JsonFolderPolicyReader>();
        services.AddSingleton<IManifestSerializer, JsonManifestSerializer>();
        return services;
    }
}
