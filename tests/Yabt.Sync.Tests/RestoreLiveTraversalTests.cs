using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Format.Mirror;
using Yabt.Format.Zip;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RestoreLiveTraversalTests
{
    [TestMethod]
    [DataRow("", ".yabt-hist")]
    [DataRow("", "archive/history")]
    [DataRow("live", "archive/history")]
    public async Task RestoreListingCostDoesNotGrowWithHistoryOrStaging
    (
        string livePrefix,
        string historyPrefix
    )
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(new(livePrefix, historyPrefix));
        await fixture.AddArchiveObjectAsync($"{historyPrefix}/initial/file.txt");
        await fixture.AddArchiveObjectAsync(".yabt-tmp/initial/file.txt");
        var firstResult = await fixture.RestoreAsync();
        Assert.IsTrue(firstResult.Completed, firstResult.Message);
        var initialFolders = fixture.Archive.FolderVisits.Order(StringComparer.Ordinal).ToArray();
        var initialObjectCount = fixture.Archive.ListedObjectCount;
        fixture.Archive.ResetVisits();

        for (var index = 0; index < 128; index++)
        {
            await fixture.AddArchiveObjectAsync($"{historyPrefix}/old-{index}/nested/file.txt");
            await fixture.AddArchiveObjectAsync($".yabt-tmp/upload-{index}/nested/file.txt");
        }

        var secondResult = await fixture.RestoreAsync();

        Assert.IsTrue(secondResult.Completed, secondResult.Message);
        CollectionAssert.AreEqual
        (
            initialFolders,
            fixture.Archive.FolderVisits.Order(StringComparer.Ordinal).ToArray()
        );
        Assert.AreEqual(initialObjectCount, fixture.Archive.ListedObjectCount);
        Assert.AreEqual(0, fixture.Archive.RecursiveRequestCount);
        Assert.IsFalse(fixture.Archive.FolderVisits.Contains(historyPrefix));
        Assert.IsFalse(fixture.Archive.FolderVisits.Contains(".yabt-tmp"));
        await fixture.AssertLivePayloadsAsync();
    }

    [TestMethod]
    [DataRow(".yabt-hist", ".yabt-hist")]
    [DataRow(".yabt-hist", ".yabt-tmp")]
    [DataRow("archive/history", "archive/history")]
    [DataRow("archive/history", ".yabt-tmp")]
    public async Task RestoreDoesNotDescendIntoUnreadableHistoryOrStaging
    (
        string historyPrefix,
        string unreadablePrefix
    )
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(new("", historyPrefix));
        await fixture.AddArchiveObjectAsync($"{unreadablePrefix}/nested/unreadable.txt");
        fixture.Archive.UnreadablePrefix = unreadablePrefix;

        var result = await fixture.RestoreAsync();

        Assert.IsTrue(result.Completed, result.Message);
        Assert.IsFalse(fixture.Archive.FolderVisits.Contains(unreadablePrefix));
        await fixture.AssertLivePayloadsAsync();
    }

    [TestMethod]
    public async Task RestorePropagatesLaterListingFailureAfterEarlierSafeCommit()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);
        fixture.Archive.UnreadablePrefix = "folder";

        var exception = await Assert.ThrowsAsync<IOException>(() => fixture.RestoreAsync());

        StringAssert.Contains(exception.Message, "Cannot list 'folder'");
        Assert.IsTrue(fixture.Archive.FolderVisits.Contains("folder"));
        Assert.AreEqual("root.txt", await File.ReadAllTextAsync(Path.Combine(fixture.DestinationRoot, "root.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
    }

    [TestMethod]
    [DataRow("folder/untracked.txt")]
    [DataRow(".yabt-root.json/untracked.txt")]
    public async Task RestoreStillRejectsUnmanifestedLiveObjects(string untrackedKey)
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);
        await fixture.AddArchiveObjectAsync(untrackedKey);

        var exception = await Assert.ThrowsAsync<YabtSyncException>(() => fixture.RestoreAsync());

        StringAssert.Contains(exception.Message, untrackedKey);
        StringAssert.Contains(exception.Message, "not covered by the change manifest");
        Assert.AreEqual("root.txt", await File.ReadAllTextAsync(Path.Combine(fixture.DestinationRoot, "root.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
    }

    [TestMethod]
    public async Task RestoreCommitsRootBeforeDiscoveringDescendants()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);
        fixture.Archive.BeforeListing = folder =>
        {
            if (folder.Length == 0) { return; }
            Assert.AreEqual("root.txt", File.ReadAllText(Path.Combine(fixture.DestinationRoot, "root.txt")));
        };

        var result = await fixture.RestoreAsync();

        Assert.IsTrue(result.Completed, result.Message);
        await fixture.AssertLivePayloadsAsync();
    }

    [TestMethod]
    public async Task IncompleteFolderNeverWritesItsPartiallyListedPayloads()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);
        fixture.Archive.AfterListedItem = folder =>
        {
            if (folder == "folder") { throw new IOException("Incomplete folder listing."); }
        };

        await Assert.ThrowsAsync<IOException>(() => fixture.RestoreAsync());

        Assert.IsTrue(File.Exists(Path.Combine(fixture.DestinationRoot, "root.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, "folder", "important.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
    }

    [TestMethod]
    public async Task FailedLaterPayloadPreservesCheckpointAndDefersExtrasUntilRerun()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);
        Directory.CreateDirectory(fixture.DestinationRoot);
        var extra = Path.Combine(fixture.DestinationRoot, "extra.txt");
        await File.WriteAllTextAsync(extra, "preserved extra");
        fixture.Archive.BeforeRead = key =>
        {
            if (key == "folder/important.txt") { throw new IOException("Temporarily unreadable."); }
        };

        var failed = await fixture.RestoreAsync();

        Assert.IsFalse(failed.Completed);
        Assert.AreEqual("preserved extra", await File.ReadAllTextAsync(extra));
        var rootPath = Path.Combine(fixture.DestinationRoot, "root.txt");
        var checkpointTime = File.GetLastWriteTimeUtc(rootPath);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
        fixture.Archive.BeforeRead = null;
        fixture.Archive.ResetVisits();
        fixture.Archive.BeforeListing = folder =>
        {
            if (folder.StartsWith(".yabt-", StringComparison.Ordinal))
            {
                Assert.IsTrue
                (
                    File.Exists(Path.Combine(fixture.DestinationRoot, "folder", "important.txt")),
                    "Restart should finish the previously failed branch before rechecking completed siblings."
                );
            }
        };
        var resumed = await fixture.RestoreAsync();
        Assert.IsTrue(resumed.Completed, resumed.Message);
        Assert.AreEqual(checkpointTime, File.GetLastWriteTimeUtc(rootPath));
        Assert.IsFalse(File.Exists(extra));
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(fixture.RootPath, "progress"), "*.sqlite").Any());
        await fixture.AssertLivePayloadsAsync();
    }

    [TestMethod]
    public async Task CancellationBeforeChildKeepsEarlierCommitAndSuppressesFinalization()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);
        using var cancellation = new CancellationTokenSource();
        fixture.Archive.BeforeListing = folder =>
        {
            if (folder == "folder") { cancellation.Cancel(); }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RestoreAsync(cancellationToken: cancellation.Token));

        Assert.IsTrue(File.Exists(Path.Combine(fixture.DestinationRoot, "root.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
    }

    [TestMethod]
    public async Task IncrementalRestoreDryRunDoesNotCreateDestination()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);

        var result = await fixture.RestoreAsync(dryRun: true);

        Assert.IsTrue(result.Completed, result.Message);
        Assert.IsFalse(Directory.Exists(fixture.DestinationRoot));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PackageRestoreStartsAfterParentCommitAndResumesAfterPartialCancellation(bool nestedPackage)
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync
        (
            ArchiveLayout.Default,
            packagedChild: true, nestedPackage: nestedPackage
        );
        using var cancellation = new CancellationTokenSource();
        string? committedPackagePath = null;
        var packageReads = 0;
        fixture.Archive.BeforeRead = key =>
        {
            if (!key.EndsWith(".zip", StringComparison.Ordinal)) { return; }
            packageReads++;
            Assert.AreEqual("root.txt", File.ReadAllText(Path.Combine(fixture.DestinationRoot, "root.txt")));
        };
        fixture.AfterCommit = path =>
        {
            if (!path.StartsWith("folder/", StringComparison.Ordinal) || committedPackagePath is not null) { return; }
            committedPackagePath = path;
            cancellation.Cancel();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RestoreAsync(cancellationToken: cancellation.Token));

        Assert.IsNotNull(committedPackagePath);
        var checkpointPath = Path.Combine(fixture.DestinationRoot, committedPackagePath);
        var checkpointTimestamp = File.GetLastWriteTimeUtc(checkpointPath);
        Assert.AreEqual(1, packageReads);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.DestinationRoot, ArchiveLogicalStateManifest.FileName)));
        fixture.AfterCommit = null;
        var resumed = await fixture.RestoreAsync();
        Assert.IsTrue(resumed.Completed, resumed.Message);
        Assert.AreEqual(checkpointTimestamp, File.GetLastWriteTimeUtc(checkpointPath));
        Assert.AreEqual(2, packageReads, "The unfinished package is reopened as one coarse unit.");
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(fixture.RootPath, "progress"), "*.sqlite").Any());
        await fixture.AssertLivePayloadsAsync();
    }

    [TestMethod]
    public async Task MissingEmptyDirectoryRequiresPackageValidationEvenWhenFilesAreUnchanged()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default, packagedChild: true);
        Assert.IsTrue((await fixture.RestoreAsync()).Completed);
        var emptyPath = Path.Combine(fixture.DestinationRoot, "folder", "empty");
        Assert.IsTrue(Directory.Exists(emptyPath));
        Directory.Delete(emptyPath);
        await fixture.CorruptPackageAsync();

        var result = await fixture.RestoreAsync();

        Assert.IsFalse(result.Completed);
        Assert.IsFalse(Directory.Exists(emptyPath), "Corrupt package evidence must not authorize a directory mutation.");
        await fixture.AssertLivePayloadsAsync();
    }

    private sealed class RestoreTraversalFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-restore-traversal-";
        private readonly ServiceProvider _services;
        private readonly TraversalArchiveResolver _resolver;
        private readonly MemoryObjectStore _archiveStore = new(provideContentHash: true);
        private readonly ArchiveLayout _layout;
        private readonly string[] _livePaths;

        private RestoreTraversalFixture(ArchiveLayout layout)
        {
            _layout = layout;
            RootPath = Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(RootPath, "source");
            DestinationRoot = Path.Combine(RootPath, "restored");
            Archive = new(_archiveStore);
            _resolver = new(_archiveStore);
            _livePaths =
            [
                "root.txt",
                "folder/important.txt",
                "folder/nested/deep.txt",
                $"{layout.HistPrefix}-ordinary/keep.txt",
                ".yabt-tmp-ordinary/keep.txt",
                .. string.IsNullOrEmpty(layout.LivePrefix) ? [] :
                    new[] { $"{layout.HistPrefix}/keep.txt", ".yabt-tmp/keep.txt" },
            ];

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            var nativeFactoryType = services.Single(service =>
                service.ServiceType == typeof(IRestoreDestinationFactory)).ImplementationType!;
            services.AddSingleton(nativeFactoryType);
            services.AddSingleton<IRestoreDestinationFactory>(provider => new ObservedDestinationFactory(
                (IRestoreDestinationFactory)provider.GetRequiredService(nativeFactoryType),
                path => AfterCommit?.Invoke(path)));
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtMetadata();
            services.AddSingleton<IBackupRootStoreResolver>(_resolver);
            services.AddYabtSync();
            services.AddSingleton<IOptionsMonitor<YabtSyncOptions>>(new RestoreOptionsMonitor(new()
            {
                ProgressDirectory = Path.Combine(RootPath, "progress"),
                ProgressFlushUnitCount = 1,
            }));
            _services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string DestinationRoot { get; }

        public ListingProbeObjectStore Archive { get; }

        public Action<string>? AfterCommit { get; set; }

        public static async Task<RestoreTraversalFixture> CreateAsync(ArchiveLayout layout, bool packagedChild = false, bool nestedPackage = false)
        {
            var fixture = new RestoreTraversalFixture(layout);
            try
            {
                Directory.CreateDirectory(fixture.SourceRoot);
                var descriptor = new BackupRootDescriptor
                (
                    BackupRootDescriptor.ExpectedDocumentType,
                    BackupRootDescriptor.ExpectedSchemaVersion,
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    layout,
                    [new("archive", TraversalArchiveResolver.Kind)]
                );
                await using (var content = File.Create(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary)))
                {
                    await fixture._services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, content);
                }
                foreach (var livePath in fixture._livePaths)
                {
                    var physicalPath = Path.Combine(fixture.SourceRoot, layout.ToLiveObjectKey(livePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
                    await File.WriteAllTextAsync(physicalPath, livePath);
                }

                if (packagedChild)
                {
                    Directory.CreateDirectory(Path.Combine(fixture.SourceRoot, layout.ToLiveObjectKey("folder/empty")));
                    await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, layout.ToLiveObjectKey("folder/.yabt-policy.json")),
                        "{\"format\":\"zip\"}");
                }
                if (nestedPackage)
                {
                    await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, layout.ToLiveObjectKey("folder/nested/.yabt-policy.json")),
                        "{\"format\":\"zip\"}");
                }
                var result = await fixture._services.GetRequiredService<IArchiveSynchronizer>()
                    .BackupAsync(new(fixture.SourceRoot));
                Assert.IsTrue(result.Completed, result.Message);
                fixture._resolver.Store = fixture.Archive;
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task<SyncRunResult> RestoreAsync(bool dryRun = false, CancellationToken cancellationToken = default) =>
            _services.GetRequiredService<IArchiveSynchronizer>().RestoreAsync(
                new(SourceRoot, DryRun: dryRun, DestinationRoot: DestinationRoot), cancellationToken);

        public async Task AddArchiveObjectAsync(string key)
        {
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("excluded or untracked payload"));
            await _archiveStore.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
        }

        public async Task CorruptPackageAsync()
        {
            var package = _archiveStore.Snapshot().Single(item => item.Key.EndsWith(".zip", StringComparison.Ordinal));
            await using var corrupt = new MemoryStream(Encoding.UTF8.GetBytes("invalid ZIP bytes"));
            Assert.IsTrue(await _archiveStore.TryReplaceIfContentHashMatchesAsync
            (
                package.Key, ArchiveHash.Compute(package.Content.Span), corrupt,
                "application/zip", new Dictionary<string, string>()
            ));
        }

        public async Task AssertLivePayloadsAsync()
        {
            foreach (var livePath in _livePaths)
            {
                var restoredPath = Path.Combine(DestinationRoot, _layout.ToLiveObjectKey(livePath));
                Assert.AreEqual(livePath, await File.ReadAllTextAsync(restoredPath));
            }
        }

        public void Dispose()
        {
            _services.Dispose();
            var fullFixturePath = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.AreEqual(temporaryRoot, Path.GetDirectoryName(fullFixturePath));
            Assert.IsTrue(Path.GetFileName(fullFixturePath).StartsWith(FixturePrefix, StringComparison.Ordinal));
            if (Directory.Exists(fullFixturePath))
            {
                Directory.Delete(fullFixturePath, recursive: true);
            }
        }
    }

    private sealed class TraversalArchiveResolver(IObjectStore store) : IBackupRootStoreResolver
    {
        public const string Kind = "restoreTraversalTest";

        public IObjectStore Store { get; set; } = store;

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => Store;
    }

    private sealed class ObservedDestinationFactory
    (
        IRestoreDestinationFactory inner,
        Action<string> afterCommit
    ) : IRestoreDestinationFactory
    {
        public IRestoreDestination Create(string rootPath, IObjectStore? store = null) =>
            new ObservedDestination(inner.Create(rootPath, store), afterCommit);

        public Stream CreateComparisonReplayStream() => inner.CreateComparisonReplayStream();
    }

    private sealed class ObservedDestination
    (
        IRestoreDestination inner,
        Action<string> afterCommit
    ) : IRestoreDestination
    {
        public string RootPath => inner.RootPath;
        public IObjectStore Store => inner.Store;
        public IObjectStoreNamespace Namespace => inner.Namespace;
        public Task EnsureSafeAsync(string? livePrefix, string? historyPrefix, CancellationToken cancellationToken = default) =>
            inner.EnsureSafeAsync(livePrefix, historyPrefix, cancellationToken);
        public void ValidateRelativePath(string relativePath) => inner.ValidateRelativePath(relativePath);
        public bool DirectoryExists(ObjectPath path) => inner.DirectoryExists(path);
        public Task CreateDirectoryAsync(string? relativePath, CancellationToken cancellationToken = default) =>
            inner.CreateDirectoryAsync(relativePath, cancellationToken);
        public Task<IObjectStoreStagingArea> CreateStagingAreaAsync(CancellationToken cancellationToken = default) =>
            inner.CreateStagingAreaAsync(cancellationToken);
        public async Task CommitStagedFileAsync
        (
            string relativePath, string stagingToken,
            DateTimeOffset? lastModifiedUtc, CancellationToken cancellationToken = default
        )
        {
            await inner.CommitStagedFileAsync(relativePath, stagingToken, lastModifiedUtc, cancellationToken);
            afterCommit(relativePath);
        }
    }

    private sealed class RestoreOptionsMonitor(YabtSyncOptions value) : IOptionsMonitor<YabtSyncOptions>
    {
        public YabtSyncOptions CurrentValue => value;
        public YabtSyncOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<YabtSyncOptions, string?> listener) => null;
    }

    private sealed class ListingProbeObjectStore(IObjectStore _inner) : IObjectStore, IObjectStoreOperationIdentity
    {
        public List<string> FolderVisits { get; } = [];

        public string OperationIdentity { get; } = "restore-traversal:" + Guid.NewGuid().ToString("N");

        public string? UnreadablePrefix { get; set; }

        public Action<string>? BeforeListing { get; set; }

        public Action<string>? AfterListedItem { get; set; }

        public Action<string>? BeforeRead { get; set; }

        public int ListedObjectCount { get; private set; }

        public int RecursiveRequestCount { get; private set; }

        public IObjectStoreNamespace Namespace => _inner.Namespace;

        public void ResetVisits()
        {
            FolderVisits.Clear();
            ListedObjectCount = 0;
            RecursiveRequestCount = 0;
        }

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = ArchiveLayout.NormalizeObjectKey(folderPrefix);
            FolderVisits.Add(prefix);
            BeforeListing?.Invoke(prefix);
            if (recursive) { RecursiveRequestCount++; }
            if (string.Equals(prefix, UnreadablePrefix, StringComparison.Ordinal))
            {
                throw new IOException($"Cannot list '{prefix}'.");
            }

            var items = _inner.GetFolderItemsAsync(folderPrefix, cancellationToken: cancellationToken);
            await foreach (var item in items)
            {
                if (item.IsObject) { ListedObjectCount++; }
                yield return item;
                AfterListedItem?.Invoke(prefix);
                if (recursive && item.IsFolder)
                {
                    var descendants = GetFolderItemsAsync(item.Key, recursive: true, cancellationToken);
                    await foreach (var descendant in descendants)
                    {
                        yield return descendant;
                    }
                }
            }
        }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            _inner.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            _inner.InitializeAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke(key);
            return _inner.OpenReadAsync(key, cancellationToken);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.ExistsAsync(key, cancellationToken);

        public Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        ) => _inner.UploadAsync(key, content, contentType, metadata, cancellationToken);

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default) =>
            _inner.MoveAsync(source, destination, cancellationToken);

        public Task MoveFolderAsync
        (
            string sourcePrefix,
            string destinationPrefix,
            CancellationToken cancellationToken = default
        ) => _inner.MoveFolderAsync(sourcePrefix, destinationPrefix, cancellationToken);
    }
}
