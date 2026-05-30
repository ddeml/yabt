using Yabt.Core.Abstractions;
using Yabt.WebDav;
using Yabt.WebDav.Implementation;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtWebDavServiceCollectionExtensions
{
    public static IServiceCollection AddYabtWebDavObjectStore
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        var optionsBuilder = services.AddOptions<WebDavObjectStoreOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindConfiguration(configSectionPath);
        }

        services.AddSingleton<IObjectStore, WebDavObjectStore>();
        return services;
    }
}
