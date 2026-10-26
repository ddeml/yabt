using System.Runtime.CompilerServices;
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
public sealed class RestoreManifestEvidenceCollectorTests
{
    [TestMethod]
    [DataRow(16)]
    [DataRow(64)]
    public async Task FinalEvidenceListsEachFolderOnceWithoutOpeningPayloads(int folderCount)
    {
        var store = new ObservedStore();
        var files = new List<ArchiveProjectedObject>();
        var directories = new List<string>();
        for (var folder = 0; folder < folderCount; folder++)
        {
            var directory = $"folder-{folder:D3}";
            directories.Add(directory);
            for (var index = 0; index < 3; index++)
            {
                var path = $"{directory}/file-{index}.bin";
                byte[] bytes = [(byte)folder, (byte)index];
                await using var content = new MemoryStream(bytes);
                await store.Contents.UploadAsync(path, content, "application/octet-stream", new Dictionary<string, string>());
                files.Add(new
                (
                    path, _ => throw new AssertFailedException("Final evidence must not open content."),
                    ContentHash: ArchiveHash.Compute(bytes)
                ));
            }
        }
        // Nested history is pruned before its descendants are listed.
        await using (var history = new MemoryStream([9]))
        {
            await store.Contents.UploadAsync
            (
                "internal/history/old.bin", history, "application/octet-stream", new Dictionary<string, string>()
            );
        }
        await using var plan = CreatePlan(files, directories);
        using var services = CreateServices();
        using var manifest = await RestoreManifestEvidenceCollector.CreateAsync
        (
            store, new(HistPrefix: "internal/history"), plan,
            services.GetRequiredService<ILogicalStateManifestSerializer>(), NullLogger.Instance, default
        );

        Assert.AreEqual(folderCount + 2, store.Listed.Count);
        Assert.AreEqual(store.Listed.Count, store.Listed.Distinct(StringComparer.Ordinal).Count());
        Assert.IsFalse(store.Listed.Contains("internal/history"));
        Assert.AreEqual(folderCount * 3, manifest.Entries.Count());
        var expected = files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            Assert.AreEqual(expected[entry.LogicalRelativePath].ContentHash, entry.ContentHash);
            var actual = store.Contents.GetObject(entry.LogicalRelativePath);
            Assert.IsTrue(ArchiveChangeFingerprint.TryCreate(actual.Content.Length, actual.LastModifiedUtc, out var fingerprint));
            Assert.AreEqual(fingerprint, entry.StatFingerprint);
        }
    }

    [TestMethod]
    public async Task MissingDestinationFileCannotProduceSuccessEvidence()
    {
        var store = new ObservedStore();
        ArchiveProjectedObject file = new
        (
            "missing.txt", _ => throw new AssertFailedException(), ContentHash: ArchiveHash.Compute("missing"u8)
        );
        await using var plan = CreatePlan([file], []);
        using var services = CreateServices();

        var error = await Assert.ThrowsExactlyAsync<YabtSyncException>
        (
            () => RestoreManifestEvidenceCollector.CreateAsync
            (
                store, ArchiveLayout.Default, plan,
                services.GetRequiredService<ILogicalStateManifestSerializer>(), NullLogger.Instance, default
            )
        );

        StringAssert.Contains(error.Message, "missing.txt");
    }

    [TestMethod]
    public async Task IncompleteFinalListingDoesNotReturnPartialManifest()
    {
        var store = new ObservedStore { FailAfterItem = true };
        await using var bytes = new MemoryStream([1]);
        await store.Contents.UploadAsync("file.bin", bytes, "application/octet-stream", new Dictionary<string, string>());
        ArchiveProjectedObject file = new
        (
            "file.bin", _ => throw new AssertFailedException(), ContentHash: ArchiveHash.Compute(new byte[] { 1 })
        );
        await using var plan = CreatePlan([file], []);
        using var services = CreateServices();

        await Assert.ThrowsExactlyAsync<IOException>
        (
            () => RestoreManifestEvidenceCollector.CreateAsync
            (
                store, ArchiveLayout.Default, plan,
                services.GetRequiredService<ILogicalStateManifestSerializer>(), NullLogger.Instance, default
            )
        );
    }

    private static RestorePlan CreatePlan(IReadOnlyList<ArchiveProjectedObject> files, IEnumerable<string> directories)
    {
        var paths = directories.ToHashSet(StringComparer.Ordinal);
        return new
        (
            files, RestoreDirectoryIndex.Create(paths, paths, StringComparer.Ordinal), [],
            new Dictionary<string, IReadOnlyList<ArchiveRestoreProjection>>(StringComparer.Ordinal), ObjectStoreNamespace.Ordinal
        );
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        return services.BuildServiceProvider();
    }

    private sealed class ObservedStore : IObjectStore
    {
        public MemoryObjectStore Contents { get; } = new();
        public List<string> Listed { get; } = [];
        public bool FailAfterItem { get; init; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException();
        public Task<ArchiveObjectContent> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Final stat collection must not open payloads.");
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Final stat collection uses immediate listings.");

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix, bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Assert.IsFalse(recursive);
            Listed.Add(folderPrefix ?? string.Empty);
            var items = Contents.GetFolderItemsAsync(folderPrefix, recursive, cancellationToken);
            await foreach (var item in items)
            {
                yield return item;
                if (FailAfterItem) { throw new IOException("Final listing interrupted."); }
            }
        }

        public Task UploadAsync
        (
            string key, Stream content, string contentType,
            IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default
        ) => throw new AssertFailedException();
        public Task MoveAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException();
        public Task MoveFolderAsync
        (
            string sourcePrefix, string destinationPrefix, CancellationToken cancellationToken = default
        ) => throw new AssertFailedException();
    }
}
