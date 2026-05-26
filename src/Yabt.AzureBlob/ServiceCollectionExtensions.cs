using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Abstractions;

namespace Yabt.AzureBlob;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzureBlobArchiveStore
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        var optionsBuilder = services.AddOptions<AzureBlobArchiveOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindConfiguration(configSectionPath);
        }

        services.AddSingleton<IArchiveObjectStore, AzureBlobArchiveObjectStore>();
        return services;
    }
}
