using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Common;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

public static class YabtOptionsBuilderExtensions
{
    public static OptionsBuilder<TOptions> BindYabtConfiguration<TOptions>
    (
        this OptionsBuilder<TOptions> builder,
        string configSectionPath,
        IReadOnlyDictionary<string, string>? unsupportedSettings = null
    )
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        builder.Configure<IConfiguration, IServiceProvider>
        (
            (_, configuration, services) => RuntimeConfigurationWarnings.Inspect<TOptions>
            (
                configuration,
                configSectionPath,
                services.GetService<ILogger<TOptions>>() ?? NullLogger<TOptions>.Instance,
                unsupportedSettings
            )
        );
        return builder.BindConfiguration(configSectionPath);
    }
}
