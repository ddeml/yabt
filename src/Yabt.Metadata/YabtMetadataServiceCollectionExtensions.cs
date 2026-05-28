using Yabt.Metadata;
using Yabt.Metadata.Implementation;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtMetadataServiceCollectionExtensions
{
    public static IServiceCollection AddYabtMetadata(this IServiceCollection services)
    {
        services.AddSingleton<IFolderPolicyReader, JsonFolderPolicyReader>();
        services.AddSingleton<IManifestSerializer, JsonManifestSerializer>();
        return services;
    }
}
