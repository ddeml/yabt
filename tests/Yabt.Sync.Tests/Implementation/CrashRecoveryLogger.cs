using Microsoft.Extensions.Logging;

namespace Yabt.Sync.Tests.Implementation;

internal sealed class CrashRecoveryLogger<T>(CrashRecoveryGate _gate) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>
    (
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => _gate.Observe(eventId.Id, state);
}
