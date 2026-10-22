using Microsoft.Extensions.DependencyInjection.Extensions;
using Yabt.Common;
using Yabt.Common.Implementation;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtTemporaryContentServiceCollectionExtensions
{
    public static IServiceCollection AddYabtTemporaryContent
    (
        this IServiceCollection services,
        string? configSectionPath = null
    )
    {
        var optionsBuilder = services.AddOptions<TemporaryContentOptions>();
        if (configSectionPath is not null)
        {
            optionsBuilder.BindYabtConfiguration(configSectionPath);
        }

        services.TryAddSingleton<ITemporaryContentFactory, TemporaryContentFactory>();
        return services;
    }
}
