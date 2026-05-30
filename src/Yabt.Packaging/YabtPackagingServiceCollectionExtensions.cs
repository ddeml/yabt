#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtPackagingServiceCollectionExtensions
{
    public static IServiceCollection AddYabtPackaging
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        _ = configSectionPath;

        return services;
    }
}
