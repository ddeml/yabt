using Yabt.AzureBlob;
using Yabt.AzureBlob.Implementation;
using Yabt.Core.Abstractions;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtAzureBlobServiceCollectionExtensions
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
