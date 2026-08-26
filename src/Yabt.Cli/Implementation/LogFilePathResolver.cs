using System.Globalization;

namespace Yabt.Cli.Implementation;

internal enum LogFilePlatform
{
    Windows,
    Linux,
    MacOS,
    Other,
}

internal sealed record LogFileEnvironment
(
    LogFilePlatform Platform,
    string? LocalApplicationData,
    string? XdgStateHome,
    string? UserProfile
);

internal sealed record LogFileDestination
(
    string Path
);

internal static class LogFilePathResolver
{
    public static LogFileDestination Resolve
    (
        LogFileRequest request,
        string currentDirectory,
        LogFileEnvironment environment,
        DateTimeOffset timestamp,
        int processId,
        Guid invocationId
    )
    {
        if (!request.IsEnabled)
        {
            throw new ArgumentException("A log file destination requires enabled file logging.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(currentDirectory))
        {
            throw new ArgumentException("A current directory is required.", nameof(currentDirectory));
        }
        ArgumentNullException.ThrowIfNull(environment);

        if (request.ExplicitPath is not null)
        {
            var explicitPath = Path.IsPathRooted(request.ExplicitPath) ?
                Path.GetFullPath(request.ExplicitPath) :
                Path.GetFullPath(request.ExplicitPath, currentDirectory);
            return new(explicitPath);
        }

        var directory = GetDefaultDirectory(environment);
        var timestampText = timestamp.ToUniversalTime().ToString
        (
            "yyyyMMdd'T'HHmmssfffffff'Z'",
            CultureInfo.InvariantCulture
        );
        var fileName = $"yabt-{timestampText}-{processId}-{invocationId:N}.log";
        return new(Path.Combine(directory, fileName));
    }

    public static LogFileEnvironment CaptureEnvironment()
    {
        var platform = OperatingSystem.IsWindows() ? LogFilePlatform.Windows :
            OperatingSystem.IsLinux() ? LogFilePlatform.Linux :
            OperatingSystem.IsMacOS() ? LogFilePlatform.MacOS :
            LogFilePlatform.Other;

        return new
        (
            platform,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        );
    }

    internal static string GetDefaultDirectory(LogFileEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.Platform switch
        {
            LogFilePlatform.Windows => Path.Combine
            (
                RequirePath(environment.LocalApplicationData, "the local application-data folder"),
                "Yabt",
                "Logs"
            ),
            LogFilePlatform.Linux => GetLinuxDirectory(environment),
            LogFilePlatform.MacOS => Path.Combine
            (
                RequirePath(environment.UserProfile, "the user profile folder"),
                "Library",
                "Logs",
                "Yabt"
            ),
            _ => GetPortableFallbackDirectory(environment),
        };
    }

    private static string GetLinuxDirectory(LogFileEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.XdgStateHome) &&
            Path.IsPathRooted(environment.XdgStateHome))
        {
            return Path.Combine(Path.GetFullPath(environment.XdgStateHome), "yabt", "logs");
        }

        return Path.Combine
        (
            RequirePath(environment.UserProfile, "the user profile folder"),
            ".local",
            "state",
            "yabt",
            "logs"
        );
    }

    private static string GetPortableFallbackDirectory(LogFileEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.LocalApplicationData))
        {
            return Path.Combine
            (
                Path.GetFullPath(environment.LocalApplicationData),
                "Yabt",
                "Logs"
            );
        }

        return Path.Combine
        (
            RequirePath(environment.UserProfile, "the user profile folder"),
            ".local",
            "state",
            "yabt",
            "logs"
        );
    }

    private static string RequirePath(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new YabtCliException($"Could not determine {description} for the default log file.");
        }

        return Path.GetFullPath(path);
    }
}
