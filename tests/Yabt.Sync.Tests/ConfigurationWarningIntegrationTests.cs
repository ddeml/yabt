using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Metadata;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ConfigurationWarningIntegrationTests
{
    [TestMethod]
    [DataRow("mirror")]
    [DataRow("zip")]
    public async Task BackupWarnsOncePerPolicySettingAndRestorePreservesPoliciesWithoutActivatingThem(string rootFormat)
    {
        var fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"yabt-config-warnings-{Guid.NewGuid():N}"));
        var sourceRoot = Path.Combine(fixtureRoot, "source");
        var archiveRoot = Path.Combine(fixtureRoot, "archive");
        var restoreRoot = Path.Combine(fixtureRoot, "restored");
        Directory.CreateDirectory(sourceRoot);
        using var logs = new WarningCaptureFactory();
        try
        {
            var descriptor = new
            {
                documentType = "yabt.backupRoot",
                schemaVersion = 1,
                archiveId = "configuration-warning-integration",
                createdAtUtc = DateTimeOffset.UnixEpoch,
                layout = new { livePrefix = "", histPrefix = ".yabt-hist", futureLayoutSetting = true },
                stores = new[] { new { id = "archive", kind = "fileSystem", rootPath = archiveRoot } },
                futureRootSetting = true,
            };
            await File.WriteAllTextAsync
            (
                Path.Combine(sourceRoot, BackupRootFileNames.Primary),
                JsonSerializer.Serialize(descriptor)
            );
            var folders = new[] { (Path: "", Format: rootFormat), (Path: "mirror", Format: "mirror"), (Path: "zip", Format: "zip") };
            foreach (var folder in folders)
            {
                var nativePath = Path.Combine(sourceRoot, folder.Path);
                Directory.CreateDirectory(nativePath);
                await File.WriteAllTextAsync(Path.Combine(nativePath, "secret.env"), $"payload for {folder.Path}");
                await File.WriteAllTextAsync
                (
                    Path.Combine(nativePath, FolderPolicyFileNames.Primary),
                    JsonSerializer.Serialize(new
                    {
                        format = folder.Format,
                        includePatterns = new[] { "*.txt" },
                        excludePatterns = new[] { "**/secret.env" },
                        options = new { compressionLevel = "unused-private-value" },
                        futurePolicySetting = "unused-private-value",
                    })
                );
            }

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(logs);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddYabtMetadata();
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtSync();
            using var provider = services.BuildServiceProvider();
            var synchronizer = provider.GetRequiredService<IArchiveSynchronizer>();

            for (var operation = 0; operation < 2; operation++)
            {
                logs.Messages.Clear();
                var backup = await synchronizer.BackupAsync(new(sourceRoot));
                Assert.IsTrue(backup.Completed, backup.Message);
                foreach (var folder in folders)
                {
                    var policyPath = Path.Combine(sourceRoot, folder.Path, FolderPolicyFileNames.Primary);
                    var policyWarnings = logs.Messages.Where(message => message.Contains(policyPath, StringComparison.Ordinal)).ToArray();
                    Assert.HasCount(4, policyWarnings, string.Join(Environment.NewLine, policyWarnings));
                    Assert.HasCount(1, policyWarnings.Where(message => message.Contains("$.excludePatterns", StringComparison.Ordinal)));
                    Assert.IsTrue(policyWarnings.Any(message => message.Contains("Files matching these patterns may be archived.", StringComparison.Ordinal)));
                }
                Assert.IsFalse(logs.Messages.Any(message => message.Contains("unused-private-value", StringComparison.Ordinal)));
            }

            logs.Messages.Clear();
            var restore = await synchronizer.RestoreAsync(new(sourceRoot, DestinationRoot: restoreRoot));
            Assert.IsTrue(restore.Completed, restore.Message);
            Assert.IsFalse(logs.Messages.Any(message => message.Contains(FolderPolicyFileNames.Primary, StringComparison.Ordinal)));
            foreach (var folder in folders)
            {
                foreach (var name in new[] { "secret.env", FolderPolicyFileNames.Primary })
                {
                    CollectionAssert.AreEqual
                    (
                        await File.ReadAllBytesAsync(Path.Combine(sourceRoot, folder.Path, name)),
                        await File.ReadAllBytesAsync(Path.Combine(restoreRoot, folder.Path, name))
                    );
                }
            }
        }
        finally
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            Assert.IsTrue(fixtureRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(fixtureRoot).StartsWith("yabt-config-warnings-", StringComparison.Ordinal));
            if (Directory.Exists(fixtureRoot)) { Directory.Delete(fixtureRoot, recursive: true); }
        }
    }

    private sealed class WarningCaptureFactory : ILoggerFactory
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() { }

        private sealed class CaptureLogger(ConcurrentQueue<string> _messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>
            (
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                if (eventId.Id == YabtEventIds.ConfigurationSettingIgnored)
                {
                    _messages.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
