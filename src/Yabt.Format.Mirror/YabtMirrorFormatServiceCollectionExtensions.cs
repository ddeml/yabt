using Microsoft.Extensions.DependencyInjection.Extensions;
using Yabt.Core.Abstractions;
using Yabt.Format.Mirror;
using Yabt.Format.Mirror.Implementation;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtMirrorFormatServiceCollectionExtensions
{
    public static IServiceCollection AddYabtMirrorFormatProvider
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        var optionsBuilder = services.AddOptions<MirrorArchiveFormatProviderOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindConfiguration(configSectionPath);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IArchiveFormatProvider, MirrorArchiveFormatProvider>();
        return services;
    }
}
