using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.Format.Mirror;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ArchiveSynchronizerTests
{
    [TestMethod]
    public async Task SyncAsyncCopiesNewProjectedObjectToTarget()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var sourceStore = new MemoryObjectStore();
        var targetStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var result = await synchronizer.SyncAsync(CreateRequest(sourceStore, targetStore));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.NewCount);
        AssertTextObject(targetStore, "folder/file.txt", "source content");
    }

    [TestMethod]
    public async Task SyncAsyncMovesChangedTargetObjectToHistory()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var sourceStore = new MemoryObjectStore();
        var targetStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "new content");
        await UploadTextAsync(targetStore, "folder/file.txt", "old content");

        var result = await synchronizer.SyncAsync(CreateRequest(sourceStore, targetStore));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.ChangedCount);
        AssertTextObject(targetStore, "folder/file.txt", "new content");

        var historicalObjects = targetStore.Snapshot()
            .Where(candidate => candidate.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(1, historicalObjects.Length);
        Assert.AreEqual("old content", ReadText(historicalObjects[0].Content));
        Assert.IsTrue(
            historicalObjects[0].Key.EndsWith("/folder/file.txt", StringComparison.Ordinal),
            $"Historical path was '{historicalObjects[0].Key}'.");
    }

    [TestMethod]
    public async Task SyncAsyncDoesNotCopySourceHistoryWhenLivePrefixIsEmpty()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var sourceStore = new MemoryObjectStore();
        var targetStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");
        await UploadTextAsync(sourceStore, ".yabt-hist/old.txt", "historical content");

        var result = await synchronizer.SyncAsync(CreateRequest(sourceStore, targetStore));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.NewCount);
        AssertTextObject(targetStore, "folder/file.txt", "source content");
        Assert.IsFalse(targetStore.TryGetObject(".yabt-hist/old.txt", out _));
    }

    [TestMethod]
    public async Task VerifyAsyncReportsDifferencesWithoutMutatingTarget()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
        var sourceStore = new MemoryObjectStore();
        var targetStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "new content");
        await UploadTextAsync(targetStore, "folder/file.txt", "old content");

        var result = await synchronizer.VerifyAsync(CreateRequest(sourceStore, targetStore));

        Assert.IsFalse(result.Completed);
        Assert.AreEqual(1, result.ChangedCount);
        AssertTextObject(targetStore, "folder/file.txt", "old content");
        Assert.IsFalse(targetStore.Snapshot().Any(candidate =>
            candidate.Key.StartsWith(".yabt-hist/", StringComparison.Ordinal)));
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMirrorFormatProjector();
        services.AddYabtSync();

        return services;
    }

    private static SyncRunRequest CreateRequest
    (
        MemoryObjectStore sourceStore,
        MemoryObjectStore targetStore
    )
    {
        return new
        (
            "memory-source",
            SourceStore: sourceStore,
            TargetStore: targetStore,
            SourceDescriptor: CreateRoot("source-archive", "source"),
            TargetDescriptor: CreateRoot("target-archive", "target"),
            Policy: new FolderPolicy(MirrorArchiveFormatName.Value)
        );
    }

    private static BackupRootDescriptor CreateRoot(string archiveId, string rootRole)
    {
        return new
        (
            BackupRootDescriptor.ExpectedDocumentType,
            1,
            archiveId,
            new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            [
                new BackupRootStore("primary", "memory"),
            ],
            rootRole
        );
    }

    private static async Task UploadTextAsync
    (
        MemoryObjectStore store,
        string key,
        string content
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await store.UploadAsync(
            key,
            stream,
            "text/plain",
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static void AssertTextObject
    (
        MemoryObjectStore store,
        string key,
        string expectedContent
    )
    {
        var archiveObject = store.GetObject(key);
        Assert.AreEqual(expectedContent, ReadText(archiveObject.Content));
    }

    private static string ReadText(ReadOnlyMemory<byte> content)
    {
        return Encoding.UTF8.GetString(content.Span);
    }
}
