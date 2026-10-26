using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Metadata;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class NestedRestoreHistoryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestorePreservesHistoryAncestorsAndHistorizesOnlyOrdinarySiblings(bool packagedRoot)
    {
        using var fixture = await NestedHistoryFixture.CreateAsync(packagedRoot);
        await fixture.BackupAsync();
        var historyTime = File.GetLastWriteTimeUtc(fixture.SentinelPath);

        var result = await fixture.RestoreAsync();

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual("current root payload", await File.ReadAllTextAsync(fixture.RestoredRootFile));
        Assert.AreEqual("old history remains here", await File.ReadAllTextAsync(fixture.SentinelPath));
        Assert.AreEqual(historyTime, File.GetLastWriteTimeUtc(fixture.SentinelPath));
        Assert.IsFalse(File.Exists(fixture.ObsoleteFile));
        Assert.IsFalse(Directory.Exists(fixture.ObsoleteFolder));
        var historicalFile = Directory.EnumerateFiles(fixture.HistoryRoot, "obsolete.txt", SearchOption.AllDirectories).Single();
        var historicalFolderFile = Directory.EnumerateFiles(fixture.HistoryRoot, "old.txt", SearchOption.AllDirectories).Single();
        Assert.AreEqual("ordinary obsolete file", await File.ReadAllTextAsync(historicalFile));
        Assert.AreEqual("ordinary obsolete folder", await File.ReadAllTextAsync(historicalFolderFile));
        StringAssert.EndsWith
        (
            Path.GetRelativePath(fixture.HistoryRoot, historicalFile),
            Path.Combine("meta", "obsolete.txt")
        );
        StringAssert.EndsWith
        (
            Path.GetRelativePath(fixture.HistoryRoot, historicalFolderFile),
            Path.Combine("meta", "obsolete-folder", "old.txt")
        );
        Assert.IsTrue(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
        var historyFiles = Directory.EnumerateFiles(fixture.HistoryRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();

        var repeated = await fixture.RestoreAsync();

        Assert.IsTrue(repeated.Completed, repeated.Message);
        Assert.AreEqual(0, repeated.ExtraCount);
        CollectionAssert.AreEqual
        (
            historyFiles,
            Directory.EnumerateFiles(fixture.HistoryRoot, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ToArray()
        );
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LaterPayloadFailurePreservesHistoryAndOrdinarySiblings(bool packagedChild)
    {
        using var fixture = await NestedHistoryFixture.CreateAsync(packagedRoot: false);
        await fixture.WriteSourceAsync("z-broken/bad.txt", "valid child payload");
        if (packagedChild)
        {
            await fixture.WriteSourceAsync("z-broken/.yabt-policy.json", "{\"format\":\"zip\"}");
        }
        await fixture.BackupAsync();
        var corruptPath = packagedChild ?
            Directory.EnumerateFiles(fixture.ArchiveRoot, "z-broken.*.zip").Single() :
            Path.Combine(fixture.ArchiveRoot, "z-broken", "bad.txt");
        await File.WriteAllTextAsync(corruptPath, "corrupt archive payload");
        var historyTime = File.GetLastWriteTimeUtc(fixture.SentinelPath);

        var result = await fixture.RestoreAsync();

        Assert.IsFalse(result.Completed, "A corrupt later payload must prevent successful finalization.");
        Assert.AreEqual("current root payload", await File.ReadAllTextAsync(fixture.RestoredRootFile));
        Assert.AreEqual("old history remains here", await File.ReadAllTextAsync(fixture.SentinelPath));
        Assert.AreEqual(historyTime, File.GetLastWriteTimeUtc(fixture.SentinelPath));
        Assert.AreEqual("ordinary obsolete file", await File.ReadAllTextAsync(fixture.ObsoleteFile));
        Assert.AreEqual("ordinary obsolete folder", await File.ReadAllTextAsync(Path.Combine(fixture.ObsoleteFolder, "old.txt")));
        CollectionAssert.AreEqual
        (
            new[] { fixture.SentinelPath },
            Directory.EnumerateFiles(fixture.HistoryRoot, "*", SearchOption.AllDirectories).ToArray()
        );
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, "z-broken", "bad.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
    }

    private sealed class NestedHistoryFixture : IDisposable
    {
        private readonly ServiceProvider _services;

        private NestedHistoryFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "yabt-nested-restore-history-" + Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "source");
            ArchiveRoot = Path.Combine(RootPath, "archive");
            DestinationRoot = Path.Combine(RootPath, "restored");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMetadata();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtSync();
            services.AddSingleton<IOptionsMonitor<YabtSyncOptions>>(new FixtureOptionsMonitor(new()
            {
                ProgressDirectory = Path.Combine(RootPath, "progress"),
                RestoreMaximumConcurrency = 1,
            }));
            _services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string ArchiveRoot { get; }

        public string DestinationRoot { get; }

        public string HistoryRoot => Path.Combine(DestinationRoot, "meta", "history");

        public string SentinelPath => Path.Combine(HistoryRoot, "old", "sentinel.txt");

        public string ObsoleteFile => Path.Combine(DestinationRoot, "meta", "obsolete.txt");

        public string ObsoleteFolder => Path.Combine(DestinationRoot, "meta", "obsolete-folder");

        public string RestoredRootFile => Path.Combine(DestinationRoot, "root.txt");

        public static async Task<NestedHistoryFixture> CreateAsync(bool packagedRoot)
        {
            var fixture = new NestedHistoryFixture();
            try
            {
                Directory.CreateDirectory(fixture.SourceRoot);
                var store = new BackupRootStore("archive", FileSystemObjectStoreKind.Value)
                {
                    ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["rootPath"] = JsonSerializer.SerializeToElement(fixture.ArchiveRoot),
                    },
                };
                var descriptor = new BackupRootDescriptor
                (
                    BackupRootDescriptor.ExpectedDocumentType,
                    BackupRootDescriptor.ExpectedSchemaVersion,
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UnixEpoch,
                    new(HistPrefix: "meta/history"),
                    [store]
                );
                await using (var content = File.Create(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary)))
                {
                    await fixture._services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, content);
                }
                await fixture.WriteSourceAsync("root.txt", "current root payload");
                if (packagedRoot)
                {
                    await fixture.WriteSourceAsync(".yabt-policy.json", "{\"format\":\"zip\"}");
                }
                await WriteAsync(fixture.SentinelPath, "old history remains here");
                await WriteAsync(fixture.ObsoleteFile, "ordinary obsolete file");
                await WriteAsync(Path.Combine(fixture.ObsoleteFolder, "old.txt"), "ordinary obsolete folder");
                return fixture;
            }
            catch (Exception)
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task WriteSourceAsync(string key, string content) => WriteAsync(Path.Combine(SourceRoot, key), content);

        public async Task BackupAsync()
        {
            var result = await _services.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new(SourceRoot));
            Assert.IsTrue(result.Completed, result.Message);
        }

        public Task<SyncRunResult> RestoreAsync() => _services.GetRequiredService<IArchiveSynchronizer>()
            .RestoreAsync(new(SourceRoot, DestinationRoot: DestinationRoot));

        public void Dispose()
        {
            _services.Dispose();
            if (Directory.Exists(RootPath)) { Directory.Delete(RootPath, recursive: true); }
        }

        private static async Task WriteAsync(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
    }

    private sealed class FixtureOptionsMonitor(YabtSyncOptions _value) : IOptionsMonitor<YabtSyncOptions>
    {
        public YabtSyncOptions CurrentValue => _value;

        public YabtSyncOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<YabtSyncOptions, string?> listener) => null;
    }
}
