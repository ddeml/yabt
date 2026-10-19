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
            optionsBuilder.BindYabtConfiguration
            (
                configSectionPath,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [nameof(WebDavObjectStoreOptions.CredentialRef)] =
                        "Credential references are not supported and will be ignored. " +
                        "Configure UserName and Password in runtime configuration for Basic authentication; " +
                        "otherwise requests are anonymous.",
                }
            );
        }

        services.AddSingleton<WebDavObjectStore>();
        services.AddSingleton<IObjectStore>(provider =>
            provider.GetRequiredService<WebDavObjectStore>());
        services.AddSingleton<IArchiveMutableObjectStore>(provider =>
            provider.GetRequiredService<WebDavObjectStore>());
        return services;
    }
}
