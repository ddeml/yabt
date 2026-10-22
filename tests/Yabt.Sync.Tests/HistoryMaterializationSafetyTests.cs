using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class HistoryMaterializationSafetyTests
{
    private const string HistoryPrefix = ".yabt-hist";
    private const string CanonicalKey = $"{HistoryPrefix}/a/content.bin";
    private const string DuplicateKey = $"{HistoryPrefix}/b/content.bin";
    private const string ManifestKey = $"{HistoryPrefix}/{ArchiveHistoryFileNames.Manifest}";
    private const string MarkerKey = $"{HistoryPrefix}/{ArchiveHistoryManifest.InvalidationMarkerFileName}";

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task DeduplicateAsyncRevalidatesPlannedMaterializationsBeforePublishing
    (
        bool canonicalChanges,
        bool dryRun
    )
    {
        var store = new ChangingMaterializationStore();
        var content = new byte[20_000];
        Array.Fill(content, (byte)73);
        await SeedAsync(store, CanonicalKey, content);
        await SeedAsync(store, DuplicateKey, content);
        var changedKey = canonicalChanges ? CanonicalKey : DuplicateKey;
        store.AfterExists = key =>
        {
            if (key.EndsWith(ArchiveHistoryFileNames.ReferenceSuffix, StringComparison.Ordinal))
            {
                store.SharedKeys.Add(changedKey);
            }
        };
        using var services = CreateServices(store);

        await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync
            (
                new("opaque-root", DryRun: dryRun)
            )
        );

        Assert.IsTrue(store.SharedKeys.Contains(changedKey));
        Assert.AreEqual(0, store.DeletedKeys.Count);
        Assert.AreEqual(0, store.UploadedKeys.Count);
        Assert.AreEqual(2, store.Inner.Snapshot().Count);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task DeduplicateAsyncPreservesOriginalsWhenIndependenceIsLostAfterPublication
    (
        bool canonicalChanges,
        bool afterManifest
    )
    {
        var store = new ChangingMaterializationStore();
        var content = new byte[20_000];
        Array.Fill(content, (byte)73);
        await SeedAsync(store, CanonicalKey, content);
        await SeedAsync(store, DuplicateKey, content);
        var changedKey = canonicalChanges ? CanonicalKey : DuplicateKey;
        store.AfterUpload = key =>
        {
            if (afterManifest ? key == ManifestKey : key.EndsWith(ArchiveHistoryFileNames.ReferenceSuffix, StringComparison.Ordinal))
            {
                store.SharedKeys.Add(changedKey);
            }
        };
        using var services = CreateServices(store);

        await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(new("opaque-root"))
        );

        Assert.IsTrue(store.SharedKeys.Contains(changedKey));
        Assert.AreEqual(0, store.DeletedKeys.Count);
        Assert.IsTrue(await store.ExistsAsync(MarkerKey));
        CollectionAssert.AreEqual(content, store.Inner.Snapshot().Single(item => item.Key == CanonicalKey).Content.ToArray());
        CollectionAssert.AreEqual(content, store.Inner.Snapshot().Single(item => item.Key == DuplicateKey).Content.ToArray());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task DeduplicateAsyncRejectsReferenceWithoutIndependentTinyOrExcludedBacking
    (
        bool excludedPath,
        bool dryRun
    )
    {
        var store = new ChangingMaterializationStore();
        using var services = CreateServices(store);
        var backingKey = excludedPath ? $"{HistoryPrefix}/a/.yabt-policy.json" : CanonicalKey;
        await SeedReferenceGroupAsync(store, services, backingKey);
        store.SharedKeys.Add(backingKey);
        var originalKeys = store.Inner.Snapshot().Select(item => item.Key).ToArray();

        await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync
            (
                new("opaque-root", DryRun: dryRun)
            )
        );

        CollectionAssert.AreEqual(originalKeys, store.Inner.Snapshot().Select(item => item.Key).ToArray());
        Assert.AreEqual(0, store.DeletedKeys.Count);
        Assert.AreEqual(0, store.UploadedKeys.Count);
    }

    [TestMethod]
    public async Task DeduplicateAsyncRevalidatesExistingReferenceBackingBeforeCompletingCatalogRebuild()
    {
        var store = new ChangingMaterializationStore();
        using var services = CreateServices(store);
        await SeedReferenceGroupAsync(store, services, CanonicalKey);
        store.AfterUpload = key =>
        {
            if (key == ManifestKey) { store.SharedKeys.Add(CanonicalKey); }
        };

        await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync(new("opaque-root"))
        );

        Assert.IsTrue(store.SharedKeys.Contains(CanonicalKey));
        Assert.IsTrue(await store.ExistsAsync(CanonicalKey));
        Assert.IsTrue(await store.ExistsAsync(MarkerKey));
        Assert.AreEqual(0, store.DeletedKeys.Count);
    }

    private static ServiceProvider CreateServices(ChangingMaterializationStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtSync();
        services.AddSingleton<IBackupRootLocator, SyntheticRootLocator>();
        services.AddSingleton<IBackupRootStoreResolver>(new SyntheticStoreResolver(store));
        return services.BuildServiceProvider();
    }

    private static async Task SeedReferenceGroupAsync
    (
        ChangingMaterializationStore store,
        ServiceProvider services,
        string backingKey
    )
    {
        var content = new byte[128];
        Array.Fill(content, (byte)81);
        await SeedAsync(store, backingKey, content);
        var serializer = services.GetRequiredService<IHistoryReferenceSerializer>();
        var entry = new ArchiveHistoryManifestEntry
        (
            "b/content.bin",
            $"b/content.bin{ArchiveHistoryFileNames.ReferenceSuffix}",
            ArchiveHistoryEntryRepresentation.Reference,
            content.LongLength,
            ArchiveHash.Compute(content)
        );
        var reference = serializer.Create(entry);
        using var serialized = new MemoryStream();
        await serializer.WriteAsync(reference, serialized);
        await SeedAsync(store, $"{HistoryPrefix}/{entry.StoredRelativePath}", serialized.ToArray());
    }

    private static async Task SeedAsync(ChangingMaterializationStore store, string key, byte[] content)
    {
        await using var source = new MemoryStream(content, writable: false);
        await store.Inner.UploadAsync
        (
            key,
            source,
            "application/octet-stream",
            new Dictionary<string, string>(StringComparer.Ordinal)
        );
    }

    private sealed class SyntheticRootLocator : IBackupRootLocator
    {
        public Task<BackupRootLocation> LocateRootAsync
        (
            string startPath,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new BackupRootDescriptor
            (
                BackupRootDescriptor.ExpectedDocumentType,
                BackupRootDescriptor.ExpectedSchemaVersion,
                "history-materialization-tests",
                DateTimeOffset.UnixEpoch,
                ArchiveLayout.Default,
                [new("target", SyntheticStoreResolver.Kind)]
            );
            return Task.FromResult(new BackupRootLocation(startPath, descriptor));
        }
    }

    private sealed class SyntheticStoreResolver(IObjectStore _store) : IBackupRootStoreResolver
    {
        public const string Kind = "synthetic-independent-materialization";

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _store;
    }

    private sealed class ChangingMaterializationStore : IArchiveMutableObjectStore
    {
        public MemoryObjectStore Inner { get; } = new();
        public HashSet<string> SharedKeys { get; } = new(StringComparer.Ordinal);
        public List<string> DeletedKeys { get; } = [];
        public List<string> UploadedKeys { get; } = [];
        public Action<string>? AfterUpload { get; set; }
        public Action<string>? AfterExists { get; set; }

        public Task<bool> IsIndependentMaterializationAsync
        (
            string key,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(!SharedKeys.Contains(key));
        }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Inner.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Inner.InitializeAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(key, cancellationToken);

        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            var exists = await Inner.ExistsAsync(key, cancellationToken);
            AfterExists?.Invoke(key);
            return exists;
        }

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => Inner.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);

        public async Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            await Inner.UploadAsync(key, content, contentType, metadata, cancellationToken);
            UploadedKeys.Add(key);
            AfterUpload?.Invoke(key);
        }

        public Task MoveAsync(string source, string destination, CancellationToken cancellationToken = default) =>
            Inner.MoveAsync(source, destination, cancellationToken);

        public Task MoveFolderAsync(string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken = default) =>
            Inner.MoveFolderAsync(sourcePrefix, destinationPrefix, cancellationToken);

        public Task<bool> TryReplaceIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            Stream replacementContent,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        ) => Inner.TryReplaceIfContentHashMatchesAsync
        (
            key,
            expectedContentHash,
            replacementContent,
            contentType,
            metadata,
            cancellationToken
        );

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            CancellationToken cancellationToken = default
        )
        {
            DeletedKeys.Add(key);
            return Inner.TryDeleteIfContentHashMatchesAsync(key, expectedContentHash, cancellationToken);
        }

        public Task<IArchiveMutationLock> AcquireArchiveMutationLockAsync
        (
            CancellationToken cancellationToken = default,
            long? maximumDocumentBytes = null
        ) =>
            Task.FromResult<IArchiveMutationLock>(new SyntheticMutationLock());
    }

    private sealed class SyntheticMutationLock : IArchiveMutationLock
    {
        public CancellationToken LockLostToken => default;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
