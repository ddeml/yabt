using System.Text;
using Microsoft.Extensions.Logging;
using Yabt.Cli.Implementation;

namespace Yabt.Cli.Tests;

[TestClass]
public sealed class YabtFileLoggerProviderTests
{
    [TestMethod]
    public void ProviderWritesDebugAsUtf8WithRequiredContextAndExcludesTrace()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var path = Path.Combine(root, "run.log");
            using (var provider = new YabtFileLoggerProvider
            (
                new(path),
                new FixedTimeProvider(new(2026, 8, 26, 12, 34, 56, 123, TimeSpan.Zero))
            ))
            {
                var logger = provider.CreateLogger("Yabt.Tests.Category");
                logger.Log
                (
                    LogLevel.Trace,
                    new EventId(41, "TraceRead"),
                    "not written",
                    null,
                    static (state, _) => state
                );
                logger.Log
                (
                    LogLevel.Debug,
                    new EventId(42, "ReadObject"),
                    "Read Grüße.txt",
                    null,
                    static (state, _) => state
                );
            }

            var bytes = File.ReadAllBytes(path);
            Assert.IsFalse(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));

            var text = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains(text, "2026-08-26T12:34:56.123Z");
            StringAssert.Contains(text, "[Debug]");
            StringAssert.Contains(text, "EventId=42 (ReadObject)");
            StringAssert.Contains(text, "Category=Yabt.Tests.Category");
            StringAssert.Contains(text, "Read Grüße.txt");
            Assert.IsFalse(text.Contains("not written", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProviderSpecificFilterAllowsDebugWhenGeneralMinimumIsInformation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var path = Path.Combine(root, "run.log");
            using (var provider = new YabtFileLoggerProvider
            (
                new(path),
                new FixedTimeProvider(DateTimeOffset.UnixEpoch)
            ))
            {
                using var loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Information);
                    builder.AddProvider(provider);
                    builder.AddFilter<YabtFileLoggerProvider>
                    (
                        static level => level is >= LogLevel.Debug and < LogLevel.None
                    );
                });
                loggerFactory.CreateLogger("Yabt.Tests.Filter").LogDebug("debug entry");
            }

            StringAssert.Contains(File.ReadAllText(path), "debug entry");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProviderRefusesExistingDestinationWithoutChangingIt()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var path = Path.Combine(root, "run.log");
            const string existingContent = "existing content";
            File.WriteAllText(path, existingContent);

            Assert.ThrowsExactly<IOException>(() =>
            {
                using var provider = new YabtFileLoggerProvider
                (
                    new(path),
                    new FixedTimeProvider(DateTimeOffset.UnixEpoch)
                );
            });

            Assert.AreEqual(existingContent, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProviderComposesEachRecordIntoOneWrite()
    {
        var writer = new TestTextWriter();
        var errorWriter = new StringWriter();
        using var provider = new YabtFileLoggerProvider
        (
            writer,
            "test.log",
            new FixedTimeProvider(DateTimeOffset.UnixEpoch),
            errorWriter
        );
        var logger = provider.CreateLogger("Yabt.Tests.SingleWrite");

        logger.LogError(new InvalidOperationException("failure"), "one record");

        Assert.AreEqual(1, writer.WriteLineCallCount);
        StringAssert.Contains(writer.Content, "one record");
        StringAssert.Contains(writer.Content, "failure");
        Assert.AreEqual(string.Empty, errorWriter.ToString());
    }

    [TestMethod]
    public void ProviderMakesWriteAndCleanupFailuresNonFatalAndReportsOnlyOnce()
    {
        var writer = new TestTextWriter(throwOnWrite: true, throwOnDispose: true);
        var errorWriter = new StringWriter();
        using var provider = new YabtFileLoggerProvider
        (
            writer,
            "failed.log",
            new FixedTimeProvider(DateTimeOffset.UnixEpoch),
            errorWriter
        );
        var logger = provider.CreateLogger("Yabt.Tests.Failure");

        logger.LogDebug("first entry");
        logger.LogDebug("second entry");
        provider.Dispose();

        Assert.AreEqual(1, writer.WriteLineCallCount);
        Assert.AreEqual(1, writer.DisposeCallCount);
        Assert.AreEqual
        (
            1,
            CountOccurrences(errorWriter.ToString(), "File logging was disabled")
        );
        StringAssert.Contains(errorWriter.ToString(), "failed.log");
        StringAssert.Contains(errorWriter.ToString(), "simulated write failure");
    }

    [TestMethod]
    public void ProviderMakesDisposeFailureNonFatalAndReportsOnlyOnce()
    {
        var writer = new TestTextWriter(throwOnDispose: true);
        var errorWriter = new StringWriter();
        var provider = new YabtFileLoggerProvider
        (
            writer,
            "failed-on-dispose.log",
            new FixedTimeProvider(DateTimeOffset.UnixEpoch),
            errorWriter
        );

        provider.Dispose();
        provider.Dispose();

        Assert.AreEqual(1, writer.DisposeCallCount);
        Assert.AreEqual
        (
            1,
            CountOccurrences(errorWriter.ToString(), "File logging was disabled")
        );
        StringAssert.Contains(errorWriter.ToString(), "simulated dispose failure");
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yabt-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static int CountOccurrences(string value, string searchValue) =>
        value.Split(searchValue, StringSplitOptions.None).Length - 1;

    private sealed class FixedTimeProvider(DateTimeOffset _utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class TestTextWriter
    (
        bool throwOnWrite = false,
        bool throwOnDispose = false
    ) : TextWriter
    {
        private readonly StringBuilder _content = new();

        public override Encoding Encoding => Encoding.UTF8;

        public int DisposeCallCount { get; private set; }

        public int WriteLineCallCount { get; private set; }

        public string Content => _content.ToString();

        public override void WriteLine(string? value)
        {
            WriteLineCallCount++;
            if (throwOnWrite)
            {
                throw new IOException("simulated write failure");
            }

            _content.AppendLine(value);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCallCount++;
            if (throwOnDispose)
            {
                throw new IOException("simulated dispose failure");
            }

            base.Dispose(disposing);
        }
    }
}
