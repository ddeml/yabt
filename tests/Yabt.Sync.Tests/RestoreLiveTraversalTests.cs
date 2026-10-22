using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Format.Mirror;
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
    public async Task RestorePropagatesLiveFolderListingFailureBeforeDestinationMutation()
    {
        using var fixture = await RestoreTraversalFixture.CreateAsync(ArchiveLayout.Default);
        fixture.Archive.UnreadablePrefix = "folder";

        var exception = await Assert.ThrowsAsync<IOException>(() => fixture.RestoreAsync());

        StringAssert.Contains(exception.Message, "Cannot list 'folder'");
        Assert.IsTrue(fixture.Archive.FolderVisits.Contains("folder"));
        Assert.IsFalse(Directory.Exists(fixture.DestinationRoot));
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
        Assert.IsFalse(Directory.Exists(fixture.DestinationRoot));
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
                "folder/important.txt",
                $"{layout.HistPrefix}-ordinary/keep.txt",
                ".yabt-tmp-ordinary/keep.txt",
                .. string.IsNullOrEmpty(layout.LivePrefix) ? [] :
                    new[] { $"{layout.HistPrefix}/keep.txt", ".yabt-tmp/keep.txt" },
            ];

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtMetadata();
            services.AddSingleton<IBackupRootStoreResolver>(_resolver);
            services.AddYabtSync();
            _services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string DestinationRoot { get; }

        public ListingProbeObjectStore Archive { get; }

        public static async Task<RestoreTraversalFixture> CreateAsync(ArchiveLayout layout)
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

        public Task<SyncRunResult> RestoreAsync() => _services.GetRequiredService<IArchiveSynchronizer>()
            .RestoreAsync(new(SourceRoot, DestinationRoot: DestinationRoot));

        public async Task AddArchiveObjectAsync(string key)
        {
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("excluded or untracked payload"));
            await _archiveStore.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
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

    private sealed class ListingProbeObjectStore(IObjectStore _inner) : IObjectStore
    {
        public List<string> FolderVisits { get; } = [];

        public string? UnreadablePrefix { get; set; }

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

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(key, cancellationToken);

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
