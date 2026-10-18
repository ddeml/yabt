using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Format.Zip;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class LateBackupOverlapTests
{
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public async Task DetectedOverlapStopsReadsAndMutationsWhilePreservingRecoveryState
    (
        int zipDepth,
        bool failDuringStreamRead
    )
    {
        var source = new OverlapDetectingSource();
        var target = new MemoryObjectStore();
        var locationResolver = new LateOverlapLocationResolver(source);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtMirrorFormatHandler();
        services.AddYabtZipFormatHandler();
        services.AddYabtSync();
        services.AddSingleton<IArchiveLocationResolver>(locationResolver);
        services.AddSingleton<IBackupRootStoreResolver>(new LateOverlapStoreResolver(target));
        services.AddSingleton<IRestoreDestinationFactory>(new LateOverlapReplayFactory());
        using var provider = services.BuildServiceProvider();
        var synchronizer = provider.GetRequiredService<IArchiveSynchronizer>();
        var serializer = provider.GetRequiredService<IBackupRootSerializer>();
        await locationResolver.SetDocumentAsync(serializer, "original descriptor");
        await UploadAsync(source.Contents, "a.txt", "old checkpoint");
        var initialBackup = await synchronizer.BackupAsync(new("source"));
        Assert.IsTrue(initialBackup.Completed, initialBackup.Message);
        var originalDescriptor = target.GetObject(BackupRootFileNames.Primary).Content.ToArray();
        var historySerializer = provider.GetRequiredService<IHistoryManifestSerializer>();
        await using (var historyContent = new MemoryStream())
        {
            await historySerializer.WriteAsync(historySerializer.Create([]), historyContent);
            historyContent.Position = 0;
            await target.UploadAsync
            (
                ArchiveLayout.Default.ToHistoryObjectKey(ArchiveHistoryFileNames.Manifest),
                historyContent,
                "application/json",
                new Dictionary<string, string>()
            );
        }

        await using (var replacement = new MemoryStream("new completed checkpoint"u8.ToArray()))
        {
            var replaced = await source.Contents.TryReplaceIfContentHashMatchesAsync
            (
                "a.txt",
                ArchiveHash.Compute("old checkpoint"u8),
                replacement,
                "text/plain",
                new Dictionary<string, string>()
            );
            Assert.IsTrue(replaced);
        }
        await locationResolver.SetDocumentAsync(serializer, "descriptor must not be published");
        var failingPrefix = ObjectPath.Root;
        for (var depth = 0; depth < zipDepth; depth++)
        {
            failingPrefix = failingPrefix.Combine(ObjectPath.Parse(depth == 0 ? "b" : "nested"));
            locationResolver.ZipPrefixes.Add(failingPrefix.Value);
            await UploadAsync
            (
                source.Contents,
                failingPrefix.Combine(ObjectPath.Parse(FolderPolicyFileNames.Primary)).Value,
                "{\"format\":\"zip\"}"
            );
        }
        var failureKey = failingPrefix.Combine(ObjectPath.Parse("m.txt")).Value;
        var laterSiblingKey = failingPrefix.Combine(ObjectPath.Parse("z.txt")).Value;
        await UploadAsync(source.Contents, failureKey, "content behind the overlapping location");
        await UploadAsync(source.Contents, laterSiblingKey, "must not be read after overlap");
        if (zipDepth > 0)
        {
            await UploadAsync(source.Contents, "z.txt", "must not be uploaded after overlap");
        }
        await UploadAsync(target, "extra.txt", "unrelated archived content");
        source.OpenedKeys.Clear();
        source.FailureKey = failureKey;
        source.FailDuringStreamRead = failDuringStreamRead;
        source.BeforeFailure = () => Assert.AreEqual
        (
            "new completed checkpoint",
            ReadText(target, "a.txt"),
            "The overlap must be discovered after the first checkpoint has been committed."
        );

        var exception = await Assert.ThrowsAsync<YabtSyncException>
        (
            () => synchronizer.BackupAsync(new("source"))
        );

        Assert.IsTrue(ObjectStoreOverlapException.IsContainedIn(exception));
        Assert.IsTrue(source.FailureObserved);
        Assert.AreEqual("new completed checkpoint", ReadText(target, "a.txt"));
        Assert.AreEqual("unrelated archived content", ReadText(target, "extra.txt"));
        Assert.IsFalse(source.OpenedKeys.Contains(laterSiblingKey));
        Assert.IsFalse(source.OpenedKeys.Contains("z.txt"));
        Assert.IsFalse(target.TryGetObject(failureKey, out _));
        Assert.IsFalse(target.TryGetObject("z.txt", out _));
        Assert.IsFalse(target.Snapshot().Any(item => item.Key.StartsWith("b.", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(originalDescriptor, target.GetObject(BackupRootFileNames.Primary).Content.ToArray());
        Assert.IsFalse(target.TryGetObject(ArchiveChangeManifest.BrotliFileName, out _));
        Assert.IsFalse(target.TryGetObject(ArchiveChangeManifest.UncompressedFileName, out _));
        Assert.IsTrue(target.TryGetObject(ArchiveChangeManifest.InvalidationMarkerFileName, out _));
        Assert.IsTrue
        (
            target.TryGetObject
            (
                ArchiveLayout.Default.ToHistoryObjectKey(ArchiveHistoryManifest.InvalidationMarkerFileName),
                out _
            )
        );
        var historicalCheckpoint = target.Snapshot().Single
        (
            item => item.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal) &&
                item.Key.EndsWith("/a.txt", StringComparison.Ordinal)
        );
        Assert.AreEqual("old checkpoint", Encoding.UTF8.GetString(historicalCheckpoint.Content.Span));
    }

    private static string ReadText(MemoryObjectStore store, string key) =>
        Encoding.UTF8.GetString(store.GetObject(key).Content.Span);

    private static async Task UploadAsync(MemoryObjectStore store, string key, string value)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(value));
        await store.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
    }

    private sealed class OverlapDetectingSource : IObjectStore
    {
        public MemoryObjectStore Contents { get; } = new();

        public List<string> OpenedKeys { get; } = [];

        public string? FailureKey { get; set; }

        public bool FailDuringStreamRead { get; set; }

        public Action? BeforeFailure { get; set; }

        public bool FailureObserved { get; private set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            Contents.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Backup must not initialize its source.");

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            OpenedKeys.Add(key);
            if (key != FailureKey) { return Contents.OpenReadAsync(key, cancellationToken); }
            if (FailDuringStreamRead)
            {
                return Task.FromResult(new ArchiveObjectContent(new OverlapReadStream(DetectOverlap)));
            }
            throw DetectOverlap();
        }

        private IOException DetectOverlap()
        {
            BeforeFailure?.Invoke();
            FailureObserved = true;
            return new IOException
            (
                "Provider could not read the source object.",
                new AggregateException
                (
                    new IOException("An unrelated failure in the same provider batch."),
                    new IOException
                    (
                        "Resolved source location is archive storage.",
                        new ObjectStoreOverlapException("Source and archive locations overlap.")
                    )
                )
            );
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Contents.ExistsAsync(key, cancellationToken);

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => Contents.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);

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

    private sealed class OverlapReadStream(Func<Exception> _failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync
        (
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromException<int>(_failure());
    }

    private sealed class LateOverlapLocationResolver(IObjectStore _source) : IArchiveLocationResolver
    {
        private BackupRootDocument? _document;

        public HashSet<string> ZipPrefixes { get; } = new(StringComparer.Ordinal);

        public async Task SetDocumentAsync(IBackupRootSerializer serializer, string name)
        {
            var descriptor = new BackupRootDescriptor
            (
                BackupRootDescriptor.ExpectedDocumentType,
                BackupRootDescriptor.ExpectedSchemaVersion,
                "late-overlap-tests",
                DateTimeOffset.UnixEpoch,
                ArchiveLayout.Default,
                [new("target", LateOverlapStoreResolver.Kind)],
                Name: name
            );
            await using var content = new MemoryStream();
            await serializer.WriteAsync(descriptor, content);
            content.Position = 0;
            _document = await serializer.ReadDocumentAsync(content);
        }

        public Task<ArchiveSourceLocation> LocateSourceAsync
        (
            string input,
            bool requireRoot = false,
            CancellationToken cancellationToken = default
        ) => Task.FromResult
        (
            new ArchiveSourceLocation
            (
                new("opaque source root", _document!),
                ObjectPath.Root,
                ObjectPath.Root,
                "source",
                _source
            )
        );

        public void ValidateBackupLocations(ArchiveSourceLocation source, IObjectStore targetStore)
        {
            // This provider discovers the alias only while opening the later object.
        }

        public void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore)
        {
        }

        public Task<FolderPolicy> ReadPolicyAsync
        (
            ArchiveSourceLocation source,
            ObjectPath prefix,
            CancellationToken cancellationToken = default
        ) => Task.FromResult
        (
            ZipPrefixes.Contains(prefix.Value) ? new FolderPolicy(ZipArchiveFormatName.Value) : FolderPolicy.Default
        );

        public void ValidateRestoreLocations
        (
            ArchiveSourceLocation descriptorLocation,
            IRestoreDestination destination,
            IObjectStore archiveStore
        ) => throw new NotSupportedException();
    }

    private sealed class LateOverlapStoreResolver(IObjectStore _target) : IBackupRootStoreResolver
    {
        public const string Kind = "late-overlap-test";

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _target;
    }

    private sealed class LateOverlapReplayFactory : IRestoreDestinationFactory
    {
        public IRestoreDestination Create(string rootPath, IObjectStore? store = null) =>
            throw new NotSupportedException();

        public Stream CreateComparisonReplayStream() => new MemoryStream();
    }
}
