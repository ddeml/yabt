using Microsoft.Extensions.Logging;

namespace Yabt.Cli.Implementation;

internal sealed class LogFileStartup : IDisposable
{
    private readonly LogFileDestination? _destination;
    private readonly YabtFileLoggerProvider? _provider;

    private LogFileStartup(LogFileDestination? destination)
    {
        _destination = destination;
        _provider = destination is null ?
            null :
            YabtFileLoggerProvider.CreateDeferred(destination, TimeProvider.System);
    }

    public bool IsEnabled => _destination is not null;

    public string Path => _destination?.Path ??
        throw new InvalidOperationException("File logging is not enabled.");

    public static LogFileStartup Prepare(LogFileRequest request)
    {
        if (!request.IsEnabled)
        {
            return new(destination: null);
        }

        try
        {
            var destination = LogFilePathResolver.Resolve
            (
                request,
                Directory.GetCurrentDirectory(),
                LogFilePathResolver.CaptureEnvironment(),
                TimeProvider.System.GetUtcNow(),
                Environment.ProcessId,
                Guid.NewGuid()
            );
            return new(destination);
        }
        catch (YabtCliException)
        {
            throw;
        }
        catch (Exception ex) when
        (
            ex is ArgumentException or
                IOException or
                NotSupportedException or
                System.Security.SecurityException or
                UnauthorizedAccessException
        )
        {
            var pathDescription = request.ExplicitPath ?? "the default location";
            throw new YabtCliException($"Could not resolve log file '{pathDescription}'.", ex);
        }
    }

    public void Attach(ILoggingBuilder loggingBuilder)
    {
        ArgumentNullException.ThrowIfNull(loggingBuilder);
        if (_provider is not null)
        {
            loggingBuilder.AddProvider(_provider);
        }
    }

    public void Start()
    {
        if (_destination is null || _provider is null)
        {
            return;
        }

        try
        {
            _provider.Start();
        }
        catch (Exception ex) when
        (
            ex is ArgumentException or
                IOException or
                NotSupportedException or
                System.Security.SecurityException or
                UnauthorizedAccessException
        )
        {
            throw new YabtCliException($"Could not open log file '{_destination.Path}'.", ex);
        }
    }

    public void Dispose() => _provider?.Dispose();
}
