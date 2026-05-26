using Microsoft.Extensions.DependencyInjection;

namespace Yabt.Packaging;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddYabtPackaging(this IServiceCollection services)
    {
        return services;
    }
}
