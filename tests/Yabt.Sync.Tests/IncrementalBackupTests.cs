using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class IncrementalBackupTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupCommitsEachFolderBeforeEnumeratingItsDescendants(bool explicitMirrorPolicy)
    {
        using var fixture = new BackupFixture();
        await fixture.WriteSourceAsync("root.txt", "root checkpoint");
        await fixture.WriteSourceAsync("branch/child.txt", "child checkpoint");
        await fixture.WriteSourceAsync("branch/nested/deep.txt", "deep payload");
        if (explicitMirrorPolicy)
        {
            await fixture.WriteSourceAsync("branch/.yabt-policy.json", "{\"format\":\"mirror\"}");
        }
        fixture.Source.BeforeListing = prefix =>
        {
            if (prefix == "branch")
            {
                Assert.AreEqual("root checkpoint", fixture.ReadTarget("root.txt"));
            }
            if (prefix == "branch/nested")
            {
                Assert.AreEqual("child checkpoint", fixture.ReadTarget("branch/child.txt"));
            }
        };

        var result = await fixture.BackupAsync();

        Assert.IsTrue(result.Completed, result.Message);
        CollectionAssert.AreEqual(new[] { "", "branch", "branch/nested" }, fixture.Source.ListedFolders);
        foreach (var sourceObject in fixture.Source.Contents.Snapshot())
        {
            CollectionAssert.AreEqual
            (
                sourceObject.Content.ToArray(),
                fixture.Target.GetObject(sourceObject.Key).Content.ToArray(),
                sourceObject.Key
            );
        }
    }

    [TestMethod]
    public async Task IncompleteLaterListingPreservesEarlierCommitWithoutWritingTheIncompleteFolder()
    {
        using var fixture = new BackupFixture();
        await fixture.PrepareChangedRootAsync();
        await fixture.WriteSourceAsync("branch/a.txt", "must wait for the complete folder listing");
        await fixture.WriteSourceAsync("branch/z.txt", "unlisted payload");
        fixture.Source.BeforeListing = prefix =>
        {
            if (prefix == "branch") { fixture.AssertRootCheckpoint(); }
        };
        fixture.Source.AfterListedItem = (prefix, _) =>
        {
            if (prefix == "branch") { throw new IOException("The source folder listing was interrupted."); }
        };

        await Assert.ThrowsAsync<IOException>(() => fixture.BackupAsync());

        fixture.AssertIncompleteRunPreservesCheckpointAndExtras();
        Assert.IsFalse(fixture.Target.TryGetObject("branch/a.txt", out _));
        Assert.IsFalse(fixture.Target.TryGetObject("branch/z.txt", out _));
        Assert.IsFalse(fixture.Source.OpenedKeys.Contains("branch/a.txt"));
    }

    [TestMethod]
    public async Task LaterPayloadFailureLeavesReusableEarlierCommitAndDefersUnrelatedCleanup()
    {
        using var fixture = new BackupFixture();
        await fixture.PrepareChangedRootAsync();
        await fixture.WriteSourceAsync("branch/bad.txt", "eventually readable payload");
        fixture.Source.BeforeRead = key =>
        {
            if (key != "branch/bad.txt") { return; }
            fixture.AssertRootCheckpoint();
            throw new IOException("The later source payload cannot be read.");
        };

        var failed = await fixture.BackupAsync();

        Assert.IsFalse(failed.Completed);
        Assert.IsNotNull(failed.Failures);
        Assert.HasCount(1, failed.Failures);
        Assert.AreEqual("source/branch/bad.txt", failed.Failures[0].RelativePath);
        fixture.AssertIncompleteRunPreservesCheckpointAndExtras();
        var checkpointTime = fixture.Target.GetObject("root.txt").LastModifiedUtc;
        fixture.Source.BeforeRead = null;

        var resumed = await fixture.BackupAsync();

        Assert.IsTrue(resumed.Completed, resumed.Message);
        Assert.AreEqual(checkpointTime, fixture.Target.GetObject("root.txt").LastModifiedUtc);
        Assert.AreEqual("eventually readable payload", fixture.ReadTarget("branch/bad.txt"));
        Assert.IsFalse(fixture.Target.TryGetObject("extra.txt", out _));
        Assert.IsFalse(fixture.Target.TryGetObject(ArchiveChangeManifest.InvalidationMarkerFileName, out _));
        Assert.IsTrue(fixture.Target.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.AreEqual(1, fixture.Target.Snapshot().Count(item =>
            item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal) &&
            item.Key.EndsWith("/root.txt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CancellationBeforeLaterFolderPreservesEarlierCommitAndRecoveryEvidence()
    {
        using var fixture = new BackupFixture();
        await fixture.PrepareChangedRootAsync();
        await fixture.WriteSourceAsync("branch/child.txt", "not reached");
        using var cancellation = new CancellationTokenSource();
        fixture.Source.BeforeListing = prefix =>
        {
            if (prefix != "branch") { return; }
            fixture.AssertRootCheckpoint();
            cancellation.Cancel();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.BackupAsync(cancellationToken: cancellation.Token));

        fixture.AssertIncompleteRunPreservesCheckpointAndExtras();
        Assert.IsFalse(fixture.Target.TryGetObject("branch/child.txt", out _));
        Assert.IsFalse(fixture.Source.OpenedKeys.Contains("branch/child.txt"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NamespaceConflictRejectsTheWholeCurrentFolderBeforeItsWrites(bool inLaterFolder)
    {
        using var fixture = new BackupFixture();
        await fixture.PrepareChangedRootAsync();
        var prefix = inLaterFolder ? "branch/" : "";
        await fixture.WriteSourceAsync(prefix + "a.txt", "must not be copied before unit validation");
        await fixture.WriteSourceAsync(prefix + "collision", "exact object");
        await fixture.WriteSourceAsync(prefix + "collision/child.txt", "same-named child prefix");

        await Assert.ThrowsAsync<YabtCoreException>(() => fixture.BackupAsync());

        Assert.IsFalse(fixture.Target.TryGetObject(prefix + "a.txt", out _));
        Assert.IsFalse(fixture.Target.TryGetObject(prefix + "collision", out _));
        Assert.IsFalse(fixture.Target.TryGetObject(prefix + "collision/child.txt", out _));
        Assert.AreEqual("unrelated archive content", fixture.ReadTarget("extra.txt"));
        if (inLaterFolder)
        {
            fixture.AssertIncompleteRunPreservesCheckpointAndExtras();
        }
        else
        {
            Assert.AreEqual("old root content", fixture.ReadTarget("root.txt"));
            Assert.IsTrue(fixture.Target.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
            Assert.IsFalse(fixture.Target.TryGetObject(ArchiveChangeManifest.InvalidationMarkerFileName, out _));
            Assert.IsFalse(fixture.Target.Snapshot().Any(item => item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task FolderByFolderDryRunLeavesArchiveAndRecoveryMetadataUnchanged()
    {
        using var fixture = new BackupFixture();
        await fixture.PrepareChangedRootAsync();
        await fixture.WriteSourceAsync("branch/child.txt", "new child payload");
        var before = fixture.TargetSnapshot();

        var result = await fixture.BackupAsync(dryRun: true);

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual(1, result.NewCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.AreEqual(1, result.ExtraCount);
        CollectionAssert.AreEqual(before, fixture.TargetSnapshot());
        CollectionAssert.AreEqual(new[] { "", "branch" }, fixture.Source.ListedFolders);
    }

    [TestMethod]
    public async Task ResumePrioritizesUnfinishedDescendantsThenRevalidatesCompletedFolders()
    {
        using var fixture = new BackupFixture(withProgress: true);
        await PrepareInterruptedProgressAsync(fixture);
        await fixture.ReplaceSourceAsync("a/done.txt", "changed after interruption");
        fixture.Source.BeforeListing = prefix =>
        {
            if (prefix == "a") { Assert.AreEqual("pending payload", fixture.ReadTarget("b/deep/new.txt")); }
        };

        var result = await fixture.BackupAsync();

        Assert.IsTrue(result.Completed, result.Message);
        CollectionAssert.AreEqual(new[] { "", "b", "b/deep", "a" }, fixture.Source.ListedFolders);
        Assert.AreEqual("changed after interruption", fixture.ReadTarget("a/done.txt"));
        Assert.IsTrue(fixture.Target.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.ProgressDirectory!, "*.sqlite").Any());
    }

    [TestMethod]
    public async Task ResumeRevalidatesAncestorPolicyBeforeFollowingSavedFrontier()
    {
        using var fixture = new BackupFixture(withProgress: true);
        await PrepareInterruptedProgressAsync(fixture);
        await fixture.WriteSourceAsync("b/.yabt-policy.json", "{\"format\":\"unsupported-format\"}");
        fixture.Source.ReadPolicy = path => path.Value == "b" ? new("unsupported-format") : FolderPolicy.Default;

        await Assert.ThrowsAsync<YabtSyncException>(() => fixture.BackupAsync());

        CollectionAssert.AreEqual(new[] { "" }, fixture.Source.ListedFolders);
        Assert.IsFalse(fixture.Source.OpenedKeys.Contains("b/deep/new.txt"));
        Assert.IsFalse(fixture.Target.TryGetObject("b/deep/new.txt", out _));
        Assert.IsFalse(fixture.Target.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
    }

    [TestMethod]
    public async Task CollectedPayloadFailureDoesNotMarkFolderComplete()
    {
        using var fixture = new BackupFixture(withProgress: true);
        await fixture.WriteSourceAsync("a/done.txt", "completed payload");
        await fixture.WriteSourceAsync("b/bad.txt", "pending payload");
        fixture.Source.BeforeRead = key =>
        {
            if (key == "b/bad.txt") { throw new IOException("Interrupted payload."); }
        };
        var failed = await fixture.BackupAsync();
        Assert.IsFalse(failed.Completed);
        Assert.AreEqual("completed payload", fixture.ReadTarget("a/done.txt"));
        fixture.Source.BeforeRead = null;
        fixture.Source.ListedFolders.Clear();

        var result = await fixture.BackupAsync();

        Assert.IsTrue(result.Completed, result.Message);
        CollectionAssert.AreEqual(new[] { "", "b", "a" }, fixture.Source.ListedFolders);
        Assert.AreEqual("pending payload", fixture.ReadTarget("b/bad.txt"));
    }

    [TestMethod]
    public async Task CorruptProgressFallsBackToFreshTraversalWithoutLosingWork()
    {
        using var fixture = new BackupFixture(withProgress: true);
        await PrepareInterruptedProgressAsync(fixture);
        var journal = Directory.EnumerateFiles(fixture.ProgressDirectory!, "*.sqlite").Single();
        await File.WriteAllTextAsync(journal, "This is deliberately not a SQLite database.");

        var result = await fixture.BackupAsync();

        Assert.IsTrue(result.Completed, result.Message);
        CollectionAssert.AreEqual(new[] { "", "a", "b", "b/deep" }, fixture.Source.ListedFolders);
        Assert.AreEqual("completed payload", fixture.ReadTarget("a/done.txt"));
        Assert.AreEqual("pending payload", fixture.ReadTarget("b/deep/new.txt"));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task DisabledProgressAndDryRunDoNotCreateProgressStorage(bool progressEnabled, bool dryRun)
    {
        using var fixture = new BackupFixture(withProgress: true, progressEnabled: progressEnabled);
        await fixture.WriteSourceAsync("branch/file.txt", "payload");

        var result = await fixture.BackupAsync(dryRun);

        Assert.IsTrue(result.Completed, result.Message);
        Assert.IsFalse(Directory.Exists(fixture.ProgressDirectory));
        Assert.AreEqual(!dryRun, fixture.Target.TryGetObject("branch/file.txt", out _));
    }

    private static async Task PrepareInterruptedProgressAsync(BackupFixture fixture)
    {
        await fixture.WriteSourceAsync("root.txt", "root payload");
        await fixture.WriteSourceAsync("a/done.txt", "completed payload");
        await fixture.WriteSourceAsync("b/parent.txt", "completed parent");
        await fixture.WriteSourceAsync("b/deep/new.txt", "pending payload");
        fixture.Source.BeforeListing = prefix =>
        {
            if (prefix == "b/deep") { throw new IOException("Interrupted before the next folder."); }
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.BackupAsync());
        Assert.AreEqual("completed payload", fixture.ReadTarget("a/done.txt"));
        Assert.AreEqual("completed parent", fixture.ReadTarget("b/parent.txt"));
        Assert.IsFalse(fixture.Target.TryGetObject("b/deep/new.txt", out _));
        Assert.AreEqual(1, Directory.EnumerateFiles(fixture.ProgressDirectory!, "*.sqlite").Count());
        fixture.Source.BeforeListing = null;
        fixture.Source.ListedFolders.Clear();
        fixture.Source.OpenedKeys.Clear();
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly ServiceProvider _services;

        public BackupFixture(bool withProgress = false, bool progressEnabled = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtMetadata();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtSync();
            if (withProgress)
            {
                ProgressDirectory = Path.Combine(Path.GetTempPath(), "yabt-progress-test-" + Guid.NewGuid().ToString("N"));
                services.AddSingleton<IOptionsMonitor<YabtSyncOptions>>(new ProgressOptionsMonitor(new()
                {
                    ProgressDirectory = ProgressDirectory,
                    ProgressEnabled = progressEnabled,
                    ProgressFlushUnitCount = 2,
                }));
            }
            services.AddSingleton<IArchiveLocationResolver>(new TestLocationResolver(Source));
            services.AddSingleton<IBackupRootStoreResolver>
            (
                new TestStoreResolver(withProgress ? new IdentifiedTarget(Target) : Target)
            );
            services.AddSingleton<IRestoreDestinationFactory>(new ReplayFactory());
            _services = services.BuildServiceProvider();
        }

        public ObservedSource Source { get; } = new();

        public MemoryObjectStore Target { get; } = new();

        public string? ProgressDirectory { get; }

        public Task<SyncRunResult> BackupAsync(bool dryRun = false, CancellationToken cancellationToken = default) =>
            _services.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new("source", DryRun: dryRun), cancellationToken);

        public Task WriteSourceAsync(string key, string content) => WriteAsync(Source.Contents, key, content);

        public async Task ReplaceSourceAsync(string key, string content)
        {
            var existing = Source.Contents.GetObject(key);
            await using var replacement = new MemoryStream(Encoding.UTF8.GetBytes(content));
            Assert.IsTrue(await Source.Contents.TryReplaceIfContentHashMatchesAsync
            (
                key, ArchiveHash.Compute(existing.Content.Span), replacement,
                "text/plain", new Dictionary<string, string>()
            ));
        }

        public async Task PrepareChangedRootAsync()
        {
            await WriteSourceAsync("root.txt", "old root content");
            var initial = await BackupAsync();
            Assert.IsTrue(initial.Completed, initial.Message);
            await WriteAsync(Target, "extra.txt", "unrelated archive content");
            await using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("new root checkpoint"));
            Assert.IsTrue(await Source.Contents.TryReplaceIfContentHashMatchesAsync
            (
                "root.txt", ArchiveHash.Compute("old root content"u8), replacement,
                "text/plain", new Dictionary<string, string>()
            ));
            Source.ListedFolders.Clear();
            Source.OpenedKeys.Clear();
        }

        public string ReadTarget(string key) => Encoding.UTF8.GetString(Target.GetObject(key).Content.Span);

        public void AssertRootCheckpoint() => Assert.AreEqual("new root checkpoint", ReadTarget("root.txt"));

        public void AssertIncompleteRunPreservesCheckpointAndExtras()
        {
            AssertRootCheckpoint();
            Assert.AreEqual("unrelated archive content", ReadTarget("extra.txt"));
            Assert.IsFalse(Target.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
            Assert.IsFalse(Target.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
            Assert.IsTrue(Target.TryGetObject(ArchiveChangeManifest.InvalidationMarkerFileName, out _));
            var oldRoot = Target.Snapshot().Single(item =>
                item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal) &&
                item.Key.EndsWith("/root.txt", StringComparison.Ordinal));
            Assert.AreEqual("old root content", Encoding.UTF8.GetString(oldRoot.Content.Span));
        }

        public string[] TargetSnapshot() => Target.Snapshot().Select(item =>
            $"{item.Key}|{item.LastModifiedUtc:O}|{Convert.ToBase64String(item.Content.Span)}").ToArray();

        public void Dispose()
        {
            _services.Dispose();
            if (ProgressDirectory is not null && Directory.Exists(ProgressDirectory))
            {
                Directory.Delete(ProgressDirectory, recursive: true);
            }
        }

        private static async Task WriteAsync(MemoryObjectStore store, string key, string content)
        {
            await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await store.UploadAsync(key, bytes, "text/plain", new Dictionary<string, string>());
        }
    }

    private sealed class ObservedSource : IObjectStore, IObjectStoreOperationIdentity
    {
        public string OperationIdentity { get; } = "test-source:" + Guid.NewGuid().ToString("N");

        public MemoryObjectStore Contents { get; } = new();

        public List<string> ListedFolders { get; } = [];

        public List<string> OpenedKeys { get; } = [];

        public Action<string>? BeforeListing { get; set; }

        public Action<string, ArchiveFolderItem>? AfterListedItem { get; set; }

        public Action<string>? BeforeRead { get; set; }

        public Func<ObjectPath, FolderPolicy>? ReadPolicy { get; set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            Contents.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Backup must not initialize its source.");

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            OpenedKeys.Add(key);
            BeforeRead?.Invoke(key);
            return Contents.OpenReadAsync(key, cancellationToken);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Contents.ExistsAsync(key, cancellationToken);

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Assert.IsFalse(recursive, "Incremental backup must use immediate-child listings.");
            var prefix = folderPrefix ?? "";
            ListedFolders.Add(prefix);
            BeforeListing?.Invoke(prefix);
            cancellationToken.ThrowIfCancellationRequested();
            var items = Contents.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);
            await foreach (var item in items)
            {
                yield return item;
                AfterListedItem?.Invoke(prefix, item);
            }
        }

        public Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Backup must not mutate its source.");

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Backup must not mutate its source.");

        public Task MoveFolderAsync
        (
            string sourcePrefix,
            string destinationPrefix,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Backup must not mutate its source.");
    }

    private sealed class TestLocationResolver(ObservedSource _source) : IArchiveLocationResolver
    {
        public Task<ArchiveSourceLocation> LocateSourceAsync
        (
            string input,
            bool requireRoot = false,
            CancellationToken cancellationToken = default
        )
        {
            var descriptor = new BackupRootDescriptor
            (
                BackupRootDescriptor.ExpectedDocumentType, BackupRootDescriptor.ExpectedSchemaVersion,
                "incremental-backup-tests", DateTimeOffset.UnixEpoch, ArchiveLayout.Default,
                [new("target", TestStoreResolver.Kind)]
            );
            return Task.FromResult(new ArchiveSourceLocation
            (
                new("opaque source root", descriptor), ObjectPath.Root, ObjectPath.Root, "source", _source
            ));
        }

        public void ValidateBackupLocations(ArchiveSourceLocation source, IObjectStore targetStore) { }

        public void ValidateTemporaryStorageLocations
        (
            string temporaryDirectory,
            ArchiveSourceLocation? source,
            IObjectStore archiveStore,
            IRestoreDestination? destination = null
        ) { }

        public void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore) { }

        public Task<FolderPolicy> ReadPolicyAsync
        (
            ArchiveSourceLocation source,
            ObjectPath prefix,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_source.ReadPolicy?.Invoke(prefix) ?? FolderPolicy.Default);

        public void ValidateRestoreLocations
        (
            ArchiveSourceLocation descriptorLocation,
            IRestoreDestination destination,
            IObjectStore archiveStore
        ) => throw new NotSupportedException();
    }

    private sealed class TestStoreResolver(IObjectStore _target) : IBackupRootStoreResolver
    {
        public const string Kind = "incremental-backup-test";

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _target;
    }

    private sealed class ReplayFactory : IRestoreDestinationFactory
    {
        public IRestoreDestination Create(string rootPath, IObjectStore? store = null) =>
            throw new NotSupportedException();

        public Stream CreateComparisonReplayStream() => new MemoryStream();
    }

    private sealed class ProgressOptionsMonitor(YabtSyncOptions _value) : IOptionsMonitor<YabtSyncOptions>
    {
        public YabtSyncOptions CurrentValue => _value;

        public YabtSyncOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<YabtSyncOptions, string?> listener) => null;
    }

    private sealed class IdentifiedTarget(MemoryObjectStore _inner) : IArchiveMutableObjectStore, IObjectStoreOperationIdentity
    {
        public string OperationIdentity { get; } = "test-target:" + Guid.NewGuid().ToString("N");

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => _inner.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(key, cancellationToken);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => _inner.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);

        public Task UploadAsync
        (
            string key, Stream content, string contentType, IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        ) => _inner.UploadAsync(key, content, contentType, metadata, cancellationToken);

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default) =>
            _inner.MoveAsync(source, destination, cancellationToken);

        public Task MoveFolderAsync(string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken = default) =>
            _inner.MoveFolderAsync(sourcePrefix, destinationPrefix, cancellationToken);

        public Task<bool> IsIndependentMaterializationAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.IsIndependentMaterializationAsync(key, cancellationToken);

        public Task<bool> TryReplaceIfContentHashMatchesAsync
        (
            string key, string expectedContentHash, Stream replacementContent, string contentType,
            IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default
        ) => _inner.TryReplaceIfContentHashMatchesAsync
        (
            key, expectedContentHash, replacementContent, contentType, metadata, cancellationToken
        );

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key, string expectedContentHash, CancellationToken cancellationToken = default
        ) => _inner.TryDeleteIfContentHashMatchesAsync(key, expectedContentHash, cancellationToken);
    }
}
