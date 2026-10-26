using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class BackupPackageProgressTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedPackagePairRemainsPendingAndResumeRebuildsChangedSource(bool packagedRoot)
    {
        using var fixture = await PackageProgressFixture.CreateAsync(packagedRoot);
        fixture.Archive.InterruptAfterPackageUpload = true;

        var failure = await Assert.ThrowsAsync<YabtSyncException>(() => fixture.BackupAsync());

        StringAssert.Contains(failure.ToString(), "Interrupted after the package upload.");
        var partialPackage = fixture.Archive.Contents.Snapshot().Single(item => item.Key.EndsWith(".zip", StringComparison.Ordinal));
        Assert.IsFalse(fixture.Archive.Contents.TryGetObject(partialPackage.Key + ".yabt-manifest.json", out _));
        Assert.IsFalse(fixture.Archive.Contents.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        var journalPath = Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Single();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = journalPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT completed_units FROM operation WHERE singleton = 1;";
            Assert.AreEqual(0L, count.ExecuteScalar(), "An uploaded ZIP without its sibling is not a completed unit.");
            count.CommandText = "SELECT COUNT(*) FROM units;";
            Assert.AreEqual
            (
                packagedRoot ? 1L : 2L,
                count.ExecuteScalar(),
                "Only the containing folder and independently stored outer ZIP may have progress rows."
            );
            count.CommandText = "SELECT completed FROM units WHERE path = '';";
            Assert.AreEqual(0L, count.ExecuteScalar());
        }
        await fixture.ReplacePayloadAsync("source changed after interruption");
        fixture.Archive.InterruptAfterPackageUpload = false;

        var resumed = await fixture.BackupAsync();

        Assert.IsTrue(resumed.Completed, resumed.Message);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Any());
        var livePackage = fixture.Archive.Contents.Snapshot().Single(item =>
            item.Key.EndsWith(".zip", StringComparison.Ordinal) &&
            !item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal));
        Assert.AreNotEqual(partialPackage.Key, livePackage.Key);
        Assert.IsTrue(fixture.Archive.Contents.TryGetObject(livePackage.Key + ".yabt-manifest.json", out _));
        Assert.IsTrue(fixture.Archive.Contents.Snapshot().Any(item =>
            item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal) &&
            item.Key.EndsWith("/" + partialPackage.Key, StringComparison.Ordinal)));

        var restored = await fixture.RestoreAsync();

        Assert.IsTrue(restored.Completed, restored.Message);
        Assert.AreEqual
        (
            "source changed after interruption",
            await File.ReadAllTextAsync(Path.Combine(fixture.DestinationRoot, fixture.PayloadPath))
        );
        Assert.AreEqual
        (
            "unchanged nested ZIP payload",
            await File.ReadAllTextAsync(Path.Combine(fixture.DestinationRoot, fixture.InnerPayloadPath))
        );
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResumeFinishesPendingSiblingPackageBeforeRevalidatingCompletedPackage(bool changeCompletedSource)
    {
        using var fixture = await PackageProgressFixture.CreateAsync(packagedRoot: false, siblingPackages: true);
        fixture.Archive.InterruptAfterPackageUpload = true;
        fixture.Archive.InterruptPackagePrefix = "b.";

        await Assert.ThrowsAsync<YabtSyncException>(() => fixture.BackupAsync());

        var completedPackage = fixture.Archive.Contents.Snapshot().Single(item =>
            item.Key.StartsWith("a.", StringComparison.Ordinal) && item.Key.EndsWith(".zip", StringComparison.Ordinal));
        var pendingPackage = fixture.Archive.Contents.Snapshot().Single(item =>
            item.Key.StartsWith("b.", StringComparison.Ordinal) && item.Key.EndsWith(".zip", StringComparison.Ordinal));
        Assert.IsTrue(fixture.Archive.Contents.TryGetObject(completedPackage.Key + ".yabt-manifest.json", out _));
        Assert.IsFalse(fixture.Archive.Contents.TryGetObject(pendingPackage.Key + ".yabt-manifest.json", out _));
        Assert.IsFalse(fixture.Archive.Contents.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        using (var connection = OpenProgressReader(fixture.ProgressDirectory))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT completed_units FROM operation WHERE singleton = 1;";
            Assert.AreEqual(1L, command.ExecuteScalar());
            // Journal paths encode UTF-16 code units; these are the exact a/b rows.
            command.CommandText = "SELECT completed FROM units WHERE path = '0061';";
            Assert.AreEqual(1L, command.ExecuteScalar(), "Package a must be complete only after both artifacts.");
            command.CommandText = "SELECT completed FROM units WHERE path = '0062';";
            Assert.AreEqual(0L, command.ExecuteScalar(), "Package b must remain pending without its adjacent manifest.");
            command.CommandText = "SELECT completed FROM units WHERE path = '';";
            Assert.AreEqual(0L, command.ExecuteScalar(), "The containing folder remains incomplete.");
        }
        if (changeCompletedSource)
        {
            await fixture.WriteSourceAsync("a/file.txt", "changed completed package source");
        }
        await fixture.WriteSourceAsync("root.txt", "changed ordinary root file");
        fixture.Archive.InterruptAfterPackageUpload = false;
        var pendingPairCommitted = false;
        var completedPackageRevalidated = false;
        fixture.Archive.AfterUpload = key =>
        {
            if (key == pendingPackage.Key + ".yabt-manifest.json") { pendingPairCommitted = true; }
        };
        fixture.Archive.BeforeRead = ObserveCompletedPackage;
        fixture.Archive.BeforeUpload = ObserveCompletedPackage;

        var resumed = await fixture.BackupAsync();

        Assert.IsTrue(resumed.Completed, resumed.Message);
        Assert.IsTrue(pendingPairCommitted);
        Assert.IsTrue(completedPackageRevalidated, "Completed package contents must still be revalidated on resume.");
        Assert.IsFalse(Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Any());
        fixture.Archive.BeforeRead = null;
        fixture.Archive.BeforeUpload = null;
        fixture.Archive.AfterUpload = null;

        var restored = await fixture.RestoreAsync();

        Assert.IsTrue(restored.Completed, restored.Message);
        Assert.AreEqual
        (
            changeCompletedSource ? "changed completed package source" : "original a package",
            await File.ReadAllTextAsync(Path.Combine(fixture.DestinationRoot, "a", "file.txt"))
        );
        Assert.AreEqual("original b package", await File.ReadAllTextAsync(Path.Combine(fixture.DestinationRoot, "b", "file.txt")));
        Assert.AreEqual("changed ordinary root file", await File.ReadAllTextAsync(Path.Combine(fixture.DestinationRoot, "root.txt")));

        void ObserveCompletedPackage(string key)
        {
            if (!key.StartsWith("a.", StringComparison.Ordinal)) { return; }
            Assert.IsTrue(pendingPairCommitted, "Pending package b must finish before completed package a is revisited.");
            completedPackageRevalidated = true;
        }
    }

    private static SqliteConnection OpenProgressReader(string directory)
    {
        var path = Directory.EnumerateFiles(directory, "*.sqlite").Single();
        var connection = new SqliteConnection
        (
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        return connection;
    }

    private sealed class PackageProgressFixture : IDisposable
    {
        private readonly ServiceProvider _services;

        private PackageProgressFixture(bool packagedRoot)
        {
            RootPath = Path.Combine(Path.GetTempPath(), "yabt-backup-package-progress-" + Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "source");
            DestinationRoot = Path.Combine(RootPath, "restored");
            ProgressDirectory = Path.Combine(RootPath, "progress");
            PayloadPath = packagedRoot ? "file.txt" : "pack/file.txt";
            InnerPayloadPath = packagedRoot ? "inner/file.txt" : "pack/inner/file.txt";
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMetadata();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtSync();
            services.AddSingleton<IBackupRootStoreResolver>(new PackageArchiveResolver(Archive));
            services.AddSingleton<IOptionsMonitor<YabtSyncOptions>>(new FixtureOptionsMonitor(new()
            {
                ProgressDirectory = ProgressDirectory,
                ProgressFlushUnitCount = 1,
            }));
            _services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string DestinationRoot { get; }

        public string ProgressDirectory { get; }

        public string PayloadPath { get; }

        public string InnerPayloadPath { get; }

        public PackageArchiveStore Archive { get; } = new();

        public static async Task<PackageProgressFixture> CreateAsync(bool packagedRoot, bool siblingPackages = false)
        {
            var fixture = new PackageProgressFixture(packagedRoot);
            try
            {
                Directory.CreateDirectory(fixture.SourceRoot);
                var descriptor = new BackupRootDescriptor
                (
                    BackupRootDescriptor.ExpectedDocumentType,
                    BackupRootDescriptor.ExpectedSchemaVersion,
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UnixEpoch,
                    ArchiveLayout.Default,
                    [new("archive", PackageArchiveResolver.Kind)]
                );
                await using (var content = File.Create(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary)))
                {
                    await fixture._services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, content);
                }
                if (siblingPackages)
                {
                    foreach (var package in new[] { "a", "b" })
                    {
                        await fixture.WriteSourceAsync(package + "/.yabt-policy.json", "{\"format\":\"zip\"}");
                        await fixture.WriteSourceAsync(package + "/file.txt", $"original {package} package");
                    }
                    await fixture.WriteSourceAsync("root.txt", "original ordinary file");
                }
                else
                {
                    var packageRoot = packagedRoot ? fixture.SourceRoot : Path.Combine(fixture.SourceRoot, "pack");
                    await WriteAsync(Path.Combine(packageRoot, FolderPolicyFileNames.Primary), "{\"format\":\"zip\"}");
                    await WriteAsync(Path.Combine(packageRoot, "inner", FolderPolicyFileNames.Primary), "{\"format\":\"zip\"}");
                    await WriteAsync(Path.Combine(fixture.SourceRoot, fixture.PayloadPath), "original payload");
                    await WriteAsync(Path.Combine(fixture.SourceRoot, fixture.InnerPayloadPath), "unchanged nested ZIP payload");
                }
                return fixture;
            }
            catch (Exception)
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task ReplacePayloadAsync(string content) => WriteAsync(Path.Combine(SourceRoot, PayloadPath), content);

        public Task WriteSourceAsync(string path, string content) => WriteAsync(Path.Combine(SourceRoot, path), content);

        public Task<SyncRunResult> BackupAsync() => _services.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new(SourceRoot));

        public Task<SyncRunResult> RestoreAsync() => _services.GetRequiredService<IArchiveSynchronizer>()
            .RestoreAsync(new(SourceRoot, DestinationRoot: DestinationRoot, ByteForByte: true));

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

    private sealed class PackageArchiveResolver(PackageArchiveStore _archive) : IBackupRootStoreResolver
    {
        public const string Kind = "package-progress-memory";

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _archive;
    }

    private sealed class PackageArchiveStore : IArchiveMutableObjectStore, IObjectStoreOperationIdentity
    {
        public MemoryObjectStore Contents { get; } = new();

        public string OperationIdentity { get; } = "package-progress:" + Guid.NewGuid().ToString("N");

        public bool InterruptAfterPackageUpload { get; set; }

        public string? InterruptPackagePrefix { get; set; }

        public Action<string>? BeforeRead { get; set; }

        public Action<string>? BeforeUpload { get; set; }

        public Action<string>? AfterUpload { get; set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Contents.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Contents.InitializeAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke(key);
            return Contents.OpenReadAsync(key, cancellationToken);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Contents.ExistsAsync(key, cancellationToken);

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => Contents.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);

        public async Task UploadAsync
        (
            string key, Stream content, string contentType, IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            BeforeUpload?.Invoke(key);
            await Contents.UploadAsync(key, content, contentType, metadata, cancellationToken);
            AfterUpload?.Invoke(key);
            if
            (
                InterruptAfterPackageUpload && key.EndsWith(".zip", StringComparison.Ordinal) &&
                (InterruptPackagePrefix is null || key.StartsWith(InterruptPackagePrefix, StringComparison.Ordinal))
            )
            {
                throw new IOException("Interrupted after the package upload.");
            }
        }

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default) =>
            Contents.MoveAsync(source, destination, cancellationToken);

        public Task MoveFolderAsync(string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken = default) =>
            Contents.MoveFolderAsync(sourcePrefix, destinationPrefix, cancellationToken);

        public Task<bool> IsIndependentMaterializationAsync(string key, CancellationToken cancellationToken = default) =>
            Contents.IsIndependentMaterializationAsync(key, cancellationToken);

        public Task<bool> TryReplaceIfContentHashMatchesAsync
        (
            string key, string expectedContentHash, Stream replacementContent, string contentType,
            IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default
        ) => Contents.TryReplaceIfContentHashMatchesAsync
        (
            key, expectedContentHash, replacementContent, contentType, metadata, cancellationToken
        );

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key, string expectedContentHash, CancellationToken cancellationToken = default
        ) => Contents.TryDeleteIfContentHashMatchesAsync(key, expectedContentHash, cancellationToken);
    }

    private sealed class FixtureOptionsMonitor(YabtSyncOptions _value) : IOptionsMonitor<YabtSyncOptions>
    {
        public YabtSyncOptions CurrentValue => _value;

        public YabtSyncOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<YabtSyncOptions, string?> listener) => null;
    }
}
