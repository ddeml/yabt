using Microsoft.Extensions.Logging;

namespace Yabt.Profiling.Implementation;

internal sealed class ResumeProfilingLogger<T>(ResumeProfilingObserver _observer) : ILogger<T>
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
    ) => _observer.Observe(eventId.Id, state);
}
