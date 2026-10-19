using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yabt.Common.Implementation;

namespace Yabt.Common;

/// <summary>Reports ignored configuration once per setting within an asynchronous operation.</summary>
public static class ConfigurationWarnings
{
    private static readonly AsyncLocal<ConcurrentDictionary<(string Source, string Property), byte>?> Current = new();

    /// <summary>Nested consumers share the enclosing operation's warning set.</summary>
    public static IDisposable BeginOperation()
    {
        if (Current.Value is not null) { return EmptyScope.Instance; }
        Current.Value = new();
        return new OperationScope();
    }

    /// <summary>Supply setting names and effective behavior, never configuration values.</summary>
    public static void Report
    (
        ILogger logger,
        string source,
        string propertyPath,
        string behavior
    )
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!logger.IsEnabled(LogLevel.Warning)) { return; }
        if (Current.Value is { } reported && !reported.TryAdd((source, propertyPath), 0)) { return; }
        logger.LogConfigurationSettingIgnored(source, propertyPath, behavior);
    }

    private sealed class OperationScope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose() { }
    }
}
