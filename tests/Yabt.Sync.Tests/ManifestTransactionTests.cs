using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Sync.Implementation;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ManifestTransactionTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task BackupFailurePreservesRecoveryEvidenceUntilReplacementIsPublished
    (
        int failAtMutation
    )
    {
        using var services = CreateServices();
        var manifests = services.GetRequiredService<ArchiveManifestStore>();
        var serializer = services.GetRequiredService<IChangeManifestSerializer>();
        var store = new FailingManifestStore();
        using var previous = CreateChangeManifest(serializer, "old.txt");
        using var next = CreateChangeManifest(serializer, "new.txt");
        await manifests.UploadChangeManifestAsync(
            store, previous, ArchiveChangeManifest.UncompressedFileName, default);
        using var loaded = await manifests.ReadChangeManifestAsync(
            store, recoverInvalidManifest: false, default);
        var transaction = new BackupManifestTransaction(
            manifests, store, loaded, ArchiveChangeManifest.BrotliFileName, NullLogger.Instance);
        store.Reset(failAtMutation);

        async Task PublishAsync()
        {
            await transaction.InvalidateAsync(default);
            await transaction.PublishAsync(next, default);
        }

        if (failAtMutation == 0)
        {
            await PublishAsync();
        }
        else
        {
            await Assert.ThrowsAsync<Exception>(PublishAsync);
        }

        Assert.AreEqual(
            failAtMutation is >= 2 and <= 4,
            await store.ExistsAsync(ArchiveChangeManifest.InvalidationMarkerFileName));
        Assert.AreEqual(
            failAtMutation is 1 or 2,
            await store.ExistsAsync(ArchiveChangeManifest.UncompressedFileName));
        Assert.AreEqual(
            failAtMutation is 0 or 4,
            await store.ExistsAsync(ArchiveChangeManifest.BrotliFileName));
        if (failAtMutation is >= 2 and <= 4)
        {
            await Assert.ThrowsExactlyAsync<YabtSyncException>(() =>
                manifests.ReadChangeManifestAsync(
                    store, recoverInvalidManifest: false, default));
        }
        else
        {
            using var readable = await manifests.ReadChangeManifestAsync(
                store, recoverInvalidManifest: false, default);
            Assert.AreEqual(
                failAtMutation == 1 ? previous.ManifestHash : next.ManifestHash,
                readable.Manifest!.ManifestHash);
        }

        // A later successful operation can replace every interrupted control state.
        store.Reset();
        using var recovery = await manifests.ReadChangeManifestAsync(
            store, recoverInvalidManifest: true, default);
        var retry = new BackupManifestTransaction(
            manifests, store, recovery, ArchiveChangeManifest.BrotliFileName, NullLogger.Instance);
        await retry.PublishAsync(next, default);
        using var recovered = await manifests.ReadChangeManifestAsync(
            store, recoverInvalidManifest: false, default);
        Assert.AreEqual(next.ManifestHash, recovered.Manifest!.ManifestHash);
        Assert.IsFalse(await store.ExistsAsync(ArchiveChangeManifest.InvalidationMarkerFileName));
    }

    [TestMethod]
    public async Task BackupLeavesEqualManifestAndItsRepresentationUntouched()
    {
        using var services = CreateServices();
        var manifests = services.GetRequiredService<ArchiveManifestStore>();
        var serializer = services.GetRequiredService<IChangeManifestSerializer>();
        var store = new FailingManifestStore();
        using var previous = CreateChangeManifest(serializer, "same.txt");
        await manifests.UploadChangeManifestAsync(
            store, previous, ArchiveChangeManifest.UncompressedFileName, default);
        using var loaded = await manifests.ReadChangeManifestAsync(
            store, recoverInvalidManifest: false, default);
        store.Reset(failAtMutation: 1);
        var transaction = new BackupManifestTransaction(
            manifests, store, loaded, ArchiveChangeManifest.UncompressedFileName, NullLogger.Instance);

        await transaction.PublishAsync(previous, default);

        Assert.AreEqual(0, store.MutationCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task RestoreFailureKeepsUnfinishedManifestStatesRecoverable
    (
        int failAtMutation
    )
    {
        using var services = CreateServices();
        var manifests = services.GetRequiredService<ArchiveManifestStore>();
        var changeSerializer = services.GetRequiredService<IChangeManifestSerializer>();
        var logicalSerializer = services.GetRequiredService<ILogicalStateManifestSerializer>();
        var store = new FailingManifestStore();
        using var oldChangeManifest = CreateChangeManifest(changeSerializer, "old.txt");
        using var previous = CreateLogicalManifest(logicalSerializer, "old.txt");
        using var next = CreateLogicalManifest(logicalSerializer, "new.txt");
        await manifests.UploadChangeManifestAsync(
            store, oldChangeManifest, ArchiveChangeManifest.UncompressedFileName, default);
        await manifests.UploadLogicalStateManifestAsync(store, previous, default);
        using var loaded = await manifests.ReadLogicalStateManifestAsync(store, default);
        var transaction = new RestoreManifestTransaction(
            manifests, store, loaded, NullLogger.Instance);
        store.Reset(failAtMutation);

        async Task PublishAsync()
        {
            await transaction.BeginAsync(
                liveStateChanged: true, rootDescriptorNeedsWrite: false, default);
            await transaction.PublishAsync(next, default);
        }

        if (failAtMutation == 0)
        {
            await PublishAsync();
        }
        else
        {
            await Assert.ThrowsAsync<Exception>(PublishAsync);
        }

        Assert.AreEqual(
            failAtMutation >= 2,
            await store.ExistsAsync(ArchiveChangeManifest.InvalidationMarkerFileName));
        Assert.AreEqual(
            failAtMutation is >= 4 and <= 6,
            await store.ExistsAsync(ArchiveLogicalStateManifest.InvalidationMarkerFileName));
        Assert.AreEqual(
            failAtMutation is 1 or 2,
            await store.ExistsAsync(ArchiveChangeManifest.UncompressedFileName));
        Assert.AreEqual(
            failAtMutation != 5,
            await store.ExistsAsync(ArchiveLogicalStateManifest.FileName));
        using (var readable = await manifests.ReadLogicalStateManifestAsync(
            store, default))
        {
            if (failAtMutation is >= 4 and <= 6)
            {
                Assert.IsNull(readable.Manifest);
            }
            else
            {
                Assert.AreEqual(
                    failAtMutation is >= 1 and <= 3 ? previous.ManifestHash : next.ManifestHash,
                    readable.Manifest!.ManifestHash);
            }
        }

        store.Reset();
        using var recovery = await manifests.ReadLogicalStateManifestAsync(store, default);
        var retry = new RestoreManifestTransaction(
            manifests, store, recovery, NullLogger.Instance);
        await retry.BeginAsync(
            liveStateChanged: true, rootDescriptorNeedsWrite: false, default);
        await retry.PublishAsync(next, default);
        using var recovered = await manifests.ReadLogicalStateManifestAsync(store, default);
        Assert.AreEqual(next.ManifestHash, recovered.Manifest!.ManifestHash);
        Assert.IsFalse(await store.ExistsAsync(ArchiveLogicalStateManifest.InvalidationMarkerFileName));
        Assert.IsFalse(await store.ExistsAsync(ArchiveChangeManifest.InvalidationMarkerFileName));
    }

    [TestMethod]
    public async Task DescriptorOnlyRestoreInvalidatesChangeEvidenceWithoutReplacingEqualLogicalState()
    {
        using var services = CreateServices();
        var manifests = services.GetRequiredService<ArchiveManifestStore>();
        var changeSerializer = services.GetRequiredService<IChangeManifestSerializer>();
        var logicalSerializer = services.GetRequiredService<ILogicalStateManifestSerializer>();
        var store = new FailingManifestStore();
        using var oldChangeManifest = CreateChangeManifest(changeSerializer, "same.txt");
        using var logicalManifest = CreateLogicalManifest(logicalSerializer, "same.txt");
        await manifests.UploadChangeManifestAsync(
            store, oldChangeManifest, ArchiveChangeManifest.UncompressedFileName, default);
        await manifests.UploadLogicalStateManifestAsync(store, logicalManifest, default);
        using var loaded = await manifests.ReadLogicalStateManifestAsync(store, default);
        var transaction = new RestoreManifestTransaction(
            manifests, store, loaded, NullLogger.Instance);
        store.Reset();

        await transaction.BeginAsync(
            liveStateChanged: false, rootDescriptorNeedsWrite: true, default);

        Assert.IsTrue(await store.ExistsAsync(ArchiveChangeManifest.InvalidationMarkerFileName));
        Assert.IsFalse(await store.ExistsAsync(ArchiveChangeManifest.UncompressedFileName));
        Assert.IsFalse(await store.ExistsAsync(ArchiveLogicalStateManifest.InvalidationMarkerFileName));
        Assert.AreEqual(2, store.MutationCount);

        await transaction.PublishAsync(logicalManifest, default);

        Assert.AreEqual(3, store.MutationCount);
        using var readable = await manifests.ReadLogicalStateManifestAsync(store, default);
        Assert.AreEqual(logicalManifest.ManifestHash, readable.Manifest!.ManifestHash);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtSync();
        return services.BuildServiceProvider();
    }

    private static ArchiveChangeManifest CreateChangeManifest
    (
        IChangeManifestSerializer serializer,
        string path
    ) => serializer.Create(
        [new(path, ArchiveChangeFingerprint.Create(0, DateTimeOffset.UnixEpoch), 0, ArchiveHash.Compute([]))],
        "mirror");

    private static ArchiveLogicalStateManifest CreateLogicalManifest
    (
        ILogicalStateManifestSerializer serializer,
        string path
    ) => serializer.Create(
        [new(path, ArchiveChangeFingerprint.Create(0, DateTimeOffset.UnixEpoch), ArchiveHash.Compute([]))]);

    private sealed class FailingManifestStore : IArchiveMutableObjectStore
    {
        private readonly MemoryObjectStore _inner = new();
        private int _failAtMutation;

        public int MutationCount { get; private set; }

        public void Reset(int failAtMutation = 0)
        {
            MutationCount = 0;
            _failAtMutation = failAtMutation;
        }

        private void BeforeMutation()
        {
            MutationCount++;
            if (MutationCount == _failAtMutation)
            {
                throw new IOException("Injected manifest mutation failure.");
            }
        }

        public Task UploadAsync
        (
            string key,
            Stream content,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        )
        {
            BeforeMutation();
            return _inner.UploadAsync(key, content, contentType, metadata, cancellationToken);
        }

        public Task<bool> TryDeleteIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            CancellationToken cancellationToken = default
        )
        {
            BeforeMutation();
            return _inner.TryDeleteIfContentHashMatchesAsync(key, expectedContentHash, cancellationToken);
        }

        public Task<bool> TryReplaceIfContentHashMatchesAsync
        (
            string key,
            string expectedContentHash,
            Stream replacementContent,
            string contentType,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            _inner.EnsureReadyAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            _inner.InitializeAsync(cancellationToken);

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => _inner.OpenReadAsync(key, cancellationToken);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            _inner.ExistsAsync(key, cancellationToken);

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => _inner.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);

        public Task<bool> IsIndependentMaterializationAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => _inner.IsIndependentMaterializationAsync(key, cancellationToken);

        public Task MoveAsync
        (
            string source,
            string destination,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task MoveFolderAsync
        (
            string sourcePrefix,
            string destinationPrefix,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
