using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yabt.Common.Tests;

[TestClass]
public sealed class ConfigurationWarningsTests
{
    [TestMethod]
    public void RepeatedAndNestedReadsWarnOncePerSettingAndAgainInTheNextOperation()
    {
        var logger = new WarningLogger();
        using (ConfigurationWarnings.BeginOperation())
        {
            ConfigurationWarnings.Report(logger, "policy.json", "$.unknown", "The default applies.");
            using (ConfigurationWarnings.BeginOperation())
            {
                ConfigurationWarnings.Report(logger, "policy.json", "$.unknown", "The default applies.");
                ConfigurationWarnings.Report(logger, "policy.json", "$.another", "The default applies.");
            }
            ConfigurationWarnings.Report(logger, "policy.json", "$.unknown", "The default applies.");
        }
        using (ConfigurationWarnings.BeginOperation())
        {
            ConfigurationWarnings.Report(logger, "policy.json", "$.unknown", "The default applies.");
        }

        Assert.HasCount(3, logger.Messages);
        Assert.IsTrue(logger.Messages.All(item => item.Level == LogLevel.Warning));
        Assert.IsTrue(logger.Messages.All(item => item.Id == YabtEventIds.ConfigurationSettingIgnored));
        StringAssert.Contains(logger.Messages.First().Message, "policy.json");
        StringAssert.Contains(logger.Messages.First().Message, "$.unknown");
        StringAssert.Contains(logger.Messages.First().Message, "The default applies.");
    }

    [TestMethod]
    public async Task ConcurrentConsumersShareOneOperationWarningSet()
    {
        var logger = new WarningLogger();
        using var operation = ConfigurationWarnings.BeginOperation();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run
        (
            () => ConfigurationWarnings.Report(logger, "policy.json", "$.unknown", "Ignored.")
        )));

        Assert.HasCount(1, logger.Messages);
    }

    [TestMethod]
    public async Task IndependentConcurrentOperationsDoNotSuppressEachOthersWarnings()
    {
        var logger = new WarningLogger();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        async Task RunOperationAsync()
        {
            using var operation = ConfigurationWarnings.BeginOperation();
            if (Interlocked.Increment(ref started) == 2) { ready.SetResult(); }
            await ready.Task;
            ConfigurationWarnings.Report(logger, "policy.json", "$.unknown", "Ignored.");
        }

        await Task.WhenAll(RunOperationAsync(), RunOperationAsync());

        Assert.HasCount(2, logger.Messages);
    }

    private sealed class WarningLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, int Id, string Message)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Enqueue((logLevel, eventId.Id, formatter(state, exception)));
    }
}
