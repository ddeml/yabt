using Yabt.Core.Abstractions;
using Yabt.Format.Zip;
using Yabt.Format.Zip.Implementation;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtZipFormatServiceCollectionExtensions
{
    public static IServiceCollection AddYabtZipFormatProvider
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        var optionsBuilder = services.AddOptions<ZipArchiveFormatOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindConfiguration(configSectionPath);
        }

        services.AddSingleton<IArchiveFormatProvider, ZipArchiveFormatProvider>();
        return services;
    }
}
