using Microsoft.Extensions.Logging;

namespace Yabt.Common.Implementation;

internal static partial class ConfigurationWarningLogMessages
{
    [LoggerMessage
    (
        EventId = YabtEventIds.ConfigurationSettingIgnored,
        Level = LogLevel.Warning,
        Message = "Configuration '{ConfigurationSource}': setting '{PropertyPath}' is ignored. {Behavior}"
    )]
    public static partial void LogConfigurationSettingIgnored
    (
        this ILogger logger,
        string configurationSource,
        string propertyPath,
        string behavior
    );
}
