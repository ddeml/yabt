using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Yabt.Cli.Implementation;

internal sealed class YabtFileLoggerProvider : ILoggerProvider
{
    private const string FailurePrefix = "Warning: File logging was disabled";

    private readonly LogFileDestination? _destination;
    private readonly TextWriter _errorWriter;
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly List<string> _pendingRecords = [];
    private readonly TimeProvider _timeProvider;
    private bool _disabled;
    private bool _started;
    private TextWriter? _writer;
    private bool _failureReported;

    public YabtFileLoggerProvider
    (
        LogFileDestination destination,
        TimeProvider timeProvider
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _destination = destination;
        _path = destination.Path;
        _timeProvider = timeProvider;
        _errorWriter = Console.Error;
        _writer = CreateWriter(destination);
        _started = true;
    }

    internal YabtFileLoggerProvider
    (
        TextWriter writer,
        string path,
        TimeProvider timeProvider,
        TextWriter errorWriter
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(errorWriter);

        _writer = writer;
        _path = path;
        _timeProvider = timeProvider;
        _errorWriter = errorWriter;
        _started = true;
    }

    private YabtFileLoggerProvider
    (
        LogFileDestination destination,
        TimeProvider timeProvider,
        TextWriter errorWriter
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(errorWriter);

        _destination = destination;
        _path = destination.Path;
        _timeProvider = timeProvider;
        _errorWriter = errorWriter;
    }

    internal static YabtFileLoggerProvider CreateDeferred
    (
        LogFileDestination destination,
        TimeProvider timeProvider
    ) => new(destination, timeProvider, Console.Error);

    private static StreamWriter CreateWriter(LogFileDestination destination)
    {
        var directory = Path.GetDirectoryName(destination.Path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException(
                "The log file path must have a parent directory.",
                nameof(destination));
        }

        Directory.CreateDirectory(directory);
        var stream = new FileStream
        (
            destination.Path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        try
        {
            return new StreamWriter
            (
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096,
                leaveOpen: false
            )
            {
                AutoFlush = true,
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Start()
    {
        lock (_lock)
        {
            if (_disabled || _started)
            {
                return;
            }

            var destination = _destination ??
                throw new InvalidOperationException("Deferred file logging has no destination.");
            _writer = CreateWriter(destination);
            _started = true;
            foreach (var record in _pendingRecords)
            {
                if (!TryWriteRecord(record))
                {
                    break;
                }
            }
            _pendingRecords.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disabled = true;
            _pendingRecords.Clear();
            var writer = _writer;
            _writer = null;
            if (writer is null)
            {
                return;
            }

            try
            {
                writer.Dispose();
            }
            catch (Exception ex) when (IsRecoverableFileLoggingException(ex))
            {
                ReportFailure(ex);
            }
        }
    }

    private void Write
    (
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        string message,
        Exception? exception
    )
    {
        lock (_lock)
        {
            if (_disabled)
            {
                return;
            }

            var record = CreateRecord(categoryName, logLevel, eventId, message, exception);
            if (!_started)
            {
                _pendingRecords.Add(record);
                return;
            }

            _ = TryWriteRecord(record);
        }
    }

    private bool TryWriteRecord(string record)
    {
        try
        {
            _writer?.WriteLine(record);
            return _writer is not null;
        }
        catch (Exception ex) when (IsRecoverableFileLoggingException(ex))
        {
            DisableFileLogging(ex);
            return false;
        }
    }

    private string CreateRecord
    (
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        string message,
        Exception? exception
    )
    {
        var timestamp = _timeProvider.GetUtcNow().ToUniversalTime().ToString
        (
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );
        var record = new StringBuilder(256)
            .Append(timestamp)
            .Append(" [")
            .Append(logLevel)
            .Append("] EventId=")
            .Append(eventId.Id.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(eventId.Name))
        {
            record.Append(" (").Append(eventId.Name).Append(')');
        }

        record
            .Append(" Category=")
            .Append(categoryName)
            .Append(' ')
            .Append(message);
        if (exception is not null)
        {
            record.AppendLine().Append(exception);
        }

        return record.ToString();
    }

    private void DisableFileLogging(Exception exception)
    {
        _disabled = true;
        _pendingRecords.Clear();
        var writer = _writer;
        _writer = null;
        if (writer is not null)
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception ex) when (IsRecoverableFileLoggingException(ex))
            {
                // The original write failure is the most useful error to report.
            }
        }

        ReportFailure(exception);
    }

    private void ReportFailure(Exception exception)
    {
        if (_failureReported)
        {
            return;
        }

        _failureReported = true;
        try
        {
            _errorWriter.WriteLine($"{FailurePrefix} for '{_path}': {exception.Message}");
        }
        catch (Exception ex) when (IsRecoverableFileLoggingException(ex))
        {
            // A diagnostic failure must not interrupt the archive operation.
        }
    }

    private static bool IsRecoverableFileLoggingException(Exception exception) =>
        exception is IOException or
            NotSupportedException or
            ObjectDisposedException or
            System.Security.SecurityException or
            UnauthorizedAccessException;

    private sealed class FileLogger
    (
        YabtFileLoggerProvider _provider,
        string _categoryName
    ) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel is >= LogLevel.Debug and < LogLevel.None;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
            {
                return;
            }

            _provider.Write(_categoryName, logLevel, eventId, message, exception);
        }
    }
}
