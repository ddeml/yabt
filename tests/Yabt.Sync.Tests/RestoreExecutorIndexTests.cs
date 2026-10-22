using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Sync.Implementation;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RestoreExecutorIndexTests
{
    [TestMethod]
    public async Task UnrelatedHistoryMovesDoNotMultiplyComparisonWorkForEveryFileCommit()
    {
        var smallWork = await ExecuteUnrelatedHistoryWorkloadAsync(32);
        var largeWork = await ExecuteUnrelatedHistoryWorkloadAsync(128);
        Console.WriteLine($"Provider comparison work: 32 items = {smallWork}; 128 items = {largeWork}.");

        // Four times as many files and obsolete objects must not cause sixteen
        // times as much provider comparison work. The generous bound permits
        // extra linear validation while rejecting the former all-moves scan.
        Assert.IsLessThanOrEqualTo(smallWork * 6 + 64, largeWork);
        Assert.IsLessThan(128 * 64, largeWork);
    }

    [TestMethod]
    public async Task FailedBlockerMoveRemainsPendingAndPreventsCommitAndUnrelatedCleanup()
    {
        using var operation = TemporaryIndex.BeginOperation();
        using var services = CreateServices();
        var destination = new TestRestoreDestination(new CountingNamespace());
        destination.Contents.FailingMovePath = "blocker";
        await UploadAsync(destination.Contents, "blocker", "original blocking file");
        await UploadAsync(destination.Contents, "unrelated.txt", "unrelated original");
        var historySerializer = services.GetRequiredService<IHistoryManifestSerializer>();
        using var catalog = historySerializer.Create([]);
        using var catalogContent = new MemoryStream();
        await historySerializer.WriteAsync(catalog, catalogContent);
        catalogContent.Position = 0;
        await destination.Contents.UploadAsync
        (
            ArchiveLayout.Default.ToHistoryObjectKey(ArchiveHistoryFileNames.Manifest),
            catalogContent,
            "application/json",
            new Dictionary<string, string>()
        );
        var files = new[] { CreateFile("blocker/child.txt") };
        await using var plan = CreatePlan(files, destination.Namespace, ["blocker"]);
        var historyMoves = new RestoreHistoryPlan(destination.Namespace.PathComparer);
        historyMoves.Add(new("blocker", IsFolder: false));
        historyMoves.Add(new("unrelated.txt", IsFolder: false));
        using var reconciliation = CreateReconciliation(files, historyMoves);
        using var loaded = new LogicalStateManifestLoad(false, false, null);

        var error = await Assert.ThrowsExactlyAsync<YabtSyncException>(() =>
            ExecuteAsync(services, destination, plan, reconciliation, loaded));

        StringAssert.Contains(error.Message, "Restore history move failed");
        CollectionAssert.AreEqual(new[] { "blocker" }, destination.Contents.MoveAttempts);
        Assert.IsEmpty(destination.CommittedPaths);
        Assert.AreEqual("original blocking file", ReadText(destination.Contents, "blocker"));
        Assert.AreEqual("unrelated original", ReadText(destination.Contents, "unrelated.txt"));
        Assert.AreEqual(1, historyMoves.GetBlockers("blocker/child.txt", default).Count);
        Assert.AreEqual(2, historyMoves.EnumeratePending(default).Count());
        Assert.IsTrue(await destination.Contents.ExistsAsync(
            ArchiveLayout.Default.ToHistoryObjectKey(ArchiveHistoryManifest.InvalidationMarkerFileName)));
        Assert.IsFalse(await destination.Contents.ExistsAsync(ArchiveLogicalStateManifest.FileName));
        Assert.IsTrue(destination.Staging.Disposed);
        Assert.AreEqual(0, destination.Staging.Count);
    }

    private static async Task<long> ExecuteUnrelatedHistoryWorkloadAsync(int count)
    {
        using var operation = TemporaryIndex.BeginOperation();
        using var services = CreateServices();
        var objectNamespace = new CountingNamespace();
        var destination = new TestRestoreDestination(objectNamespace);
        var files = Enumerable.Range(0, count)
            .Select(index => CreateFile($"new-{index:D5}.txt"))
            .ToArray();
        await using var plan = CreatePlan(files, objectNamespace, []);
        var historyMoves = new RestoreHistoryPlan(objectNamespace.PathComparer);
        for (var index = 0; index < count; index++)
        {
            var relativePath = $"obsolete-{index:D5}.txt";
            await UploadAsync(destination.Contents, relativePath, "obsolete payload");
            historyMoves.Add(new(relativePath, IsFolder: false));
        }
        using var reconciliation = CreateReconciliation(files, historyMoves);
        using var loaded = new LogicalStateManifestLoad(false, false, null);
        destination.Contents.BeforeMove = _ => Assert.HasCount(count, destination.CommittedPaths);
        objectNamespace.Comparer.WorkCount = 0;

        var failures = await ExecuteAsync(services, destination, plan, reconciliation, loaded);

        var work = objectNamespace.Comparer.WorkCount;
        Assert.IsEmpty(failures);
        Assert.HasCount(count, destination.CommittedPaths);
        Assert.HasCount(count, destination.Contents.MoveAttempts);
        Assert.IsEmpty(historyMoves.EnumeratePending(default));
        Assert.IsTrue(destination.Staging.Disposed);
        return work;
    }

    private static Task<IReadOnlyList<SyncItemFailure>> ExecuteAsync
    (
        ServiceProvider services,
        TestRestoreDestination destination,
        RestorePlan plan,
        RestoreReconciliation reconciliation,
        LogicalStateManifestLoad loaded
    ) => services.GetRequiredService<RestoreExecutor>().ApplyRestorePlanAsync
    (
        destination.Contents,
        ArchiveLayout.Default,
        plan,
        reconciliation,
        destination,
        loaded,
        RootDescriptorRestore.None,
        archiveRootDocument: null,
        restoreRootNeedsCreation: false,
        byteForByte: false,
        restoreMaximumConcurrency: 1,
        cancellationToken: default
    );

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtSync();
        return services.BuildServiceProvider();
    }

    private static RestorePlan CreatePlan
    (
        IReadOnlyList<ArchiveProjectedObject> files,
        IObjectStoreNamespace objectNamespace,
        string[] directories
    ) => new
    (
        files,
        RestoreDirectoryIndex.Create(
            directories,
            directories.ToHashSet(objectNamespace.PathComparer),
            objectNamespace.PathComparer),
        [],
        files.ToDictionary(
            file => file.RelativePath,
            _ => (IReadOnlyList<ArchiveRestoreProjection>)[],
            StringComparer.Ordinal),
        objectNamespace
    );

    private static RestoreReconciliation CreateReconciliation
    (
        ArchiveProjectedObject[] files,
        RestoreHistoryPlan historyMoves
    ) => new
    (
        files,
        historyMoves,
        files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        NewCount: files.Length,
        ChangedCount: 0,
        ExtraCount: historyMoves.Count,
        UnchangedCount: 0,
        Failures: []
    );

    private static ArchiveProjectedObject CreateFile(string relativePath)
    {
        var bytes = "new payload"u8.ToArray();
        return new
        (
            relativePath,
            _ => Task.FromResult(new ArchiveObjectContent(new MemoryStream(bytes, writable: false))),
            ContentLength: bytes.Length,
            LastModifiedUtc: DateTimeOffset.UnixEpoch,
            ContentHash: ArchiveHash.Compute(bytes)
        );
    }

    private static async Task UploadAsync(IObjectStore store, string path, string text)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await store.UploadAsync(path, content, "text/plain", new Dictionary<string, string>());
    }

    private static string ReadText(TestStore store, string path) =>
        Encoding.UTF8.GetString(store.Inner.GetObject(path).Content.Span);

    private sealed class CountingNamespace : ObjectStoreNamespace
    {
        public CountingComparer Comparer { get; } = new();

        public override StringComparer PathComparer => Comparer;

        public override void ValidatePath(ObjectPath path) { }
    }

    private sealed class CountingComparer : StringComparer
    {
        public long WorkCount { get; set; }

        public override int Compare(string? x, string? y)
        {
            WorkCount++;
            return StringComparer.Ordinal.Compare(x, y);
        }

        public override bool Equals(string? x, string? y)
        {
            WorkCount++;
            return StringComparer.Ordinal.Equals(x, y);
        }

        public override int GetHashCode(string obj)
        {
            WorkCount++;
            return StringComparer.Ordinal.GetHashCode(obj);
        }
    }

    private sealed class TestRestoreDestination(IObjectStoreNamespace objectNamespace) : IRestoreDestination
    {
        public TestStore Contents { get; } = new(objectNamespace);
        public TestStagingArea Staging { get; } = new();
        public List<string> CommittedPaths { get; } = [];
        public string RootPath => "restore-index-test";
        public IObjectStore Store => Contents;
        public IObjectStoreNamespace Namespace => Contents.Namespace;

        public Task EnsureSafeAsync
        (
            string? livePrefix,
            string? historyPrefix,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public void ValidateRelativePath(string relativePath) => Namespace.ValidatePath(ObjectPath.Parse(relativePath));

        public bool DirectoryExists(ObjectPath path) => path.IsRoot;

        public Task CreateDirectoryAsync(string? relativePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IObjectStoreStagingArea> CreateStagingAreaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IObjectStoreStagingArea>(Staging);

        public async Task CommitStagedFileAsync
        (
            string relativePath,
            string stagingToken,
            DateTimeOffset? lastModifiedUtc,
            CancellationToken cancellationToken = default
        )
        {
            using var content = new MemoryStream(Staging.Take(stagingToken), writable: false);
            await Contents.UploadAsync(
                relativePath, content, "text/plain", new Dictionary<string, string>(), cancellationToken);
            CommittedPaths.Add(relativePath);
        }
    }

    private sealed class TestStagingArea : IObjectStoreStagingArea
    {
        private readonly Dictionary<string, MemoryStream> _files = new(StringComparer.Ordinal);
        private int _nextToken;

        public bool Disposed { get; private set; }
        public int Count => _files.Count;

        public string CreateFileToken() => (++_nextToken).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public Stream OpenWrite(string stagingToken)
        {
            var stream = new MemoryStream();
            _files.Add(stagingToken, stream);
            return stream;
        }

        public byte[] Take(string stagingToken)
        {
            var result = _files[stagingToken].ToArray();
            DeleteFile(stagingToken);
            return result;
        }

        public void DeleteFile(string stagingToken)
        {
            if (_files.Remove(stagingToken, out var stream)) { stream.Dispose(); }
        }

        public ValueTask DisposeAsync()
        {
            foreach (var stream in _files.Values) { stream.Dispose(); }
            _files.Clear();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestStore(IObjectStoreNamespace _namespace) : IArchiveMutableObjectStore
    {
        public MemoryObjectStore Inner { get; } = new();
        public List<string> MoveAttempts { get; } = [];
        public string? FailingMovePath { get; set; }
        public Action<string>? BeforeMove { get; set; }
        public IObjectStoreNamespace Namespace => _namespace;

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            Inner.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Inner.InitializeAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(key, cancellationToken);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Inner.ExistsAsync(key, cancellationToken);

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => Inner.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);

        public Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        ) => Inner.UploadAsync(key, content, contentType, metadata, cancellationToken);

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default)
        {
            MoveAttempts.Add(source);
            BeforeMove?.Invoke(source);
            if (source == FailingMovePath) { throw new IOException("Injected blocker move failure."); }
            return Inner.MoveAsync(source, destination, cancellationToken);
        }

        public Task MoveFolderAsync(string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken = default) =>
            Inner.MoveFolderAsync(sourcePrefix, destinationPrefix, cancellationToken);

        public Task<bool> IsIndependentMaterializationAsync(string key, CancellationToken cancellationToken = default) =>
            Inner.IsIndependentMaterializationAsync(key, cancellationToken);

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            CancellationToken cancellationToken = default
        ) => Inner.TryDeleteIfContentHashMatchesAsync(key, expectedContentHash, cancellationToken);

        public Task<bool> TryReplaceIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            Stream replacementContent,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        ) => Inner.TryReplaceIfContentHashMatchesAsync(
            key, expectedContentHash, replacementContent, contentType, metadata, cancellationToken);
    }
}
