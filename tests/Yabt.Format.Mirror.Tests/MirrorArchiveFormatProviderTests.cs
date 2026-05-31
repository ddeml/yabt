using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Format.Mirror;
using Yabt.Tests;

namespace Yabt.Format.Mirror.Tests;

[TestClass]
public sealed class MirrorArchiveFormatProviderTests
{
    [TestMethod]
    public void ServiceRegistrationRegistersMirrorFormatProvider()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();

        var providers = serviceProvider.GetServices<IArchiveFormatProvider>().ToArray();

        Assert.AreEqual(1, providers.Length);
        var provider = providers[0];
        Assert.AreEqual(MirrorArchiveFormatName.Value, provider.FormatName);
    }

    [TestMethod]
    public async Task BackupAsyncCopiesNewLiveObjectsToTarget()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var providers = serviceProvider.GetServices<IArchiveFormatProvider>().ToArray();
        Assert.AreEqual(1, providers.Length);
        var provider = providers[0];
        var sourceStore = new InMemoryObjectStore();
        var targetStore = new InMemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var result = await provider.BackupAsync(CreateBackupRequest(sourceStore, targetStore));

        Assert.IsTrue(result.Completed);
        StringAssert.Contains(result.Message, "1 new object(s)");
        AssertTextObject(targetStore, ArchiveArea.Live, "folder/file.txt", "source content");
    }

    [TestMethod]
    public async Task RestoreAsyncMovesChangedTargetObjectToHistory()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var providers = serviceProvider.GetServices<IArchiveFormatProvider>().ToArray();
        Assert.AreEqual(1, providers.Length);
        var provider = providers[0];
        var sourceStore = new InMemoryObjectStore();
        var targetStore = new InMemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "restored content");
        await UploadTextAsync(targetStore, "folder/file.txt", "old content");

        var result = await provider.RestoreAsync(CreateRestoreRequest(sourceStore, targetStore));

        Assert.IsTrue(result.Completed);
        StringAssert.Contains(result.Message, "1 changed object(s)");
        AssertTextObject(targetStore, ArchiveArea.Live, "folder/file.txt", "restored content");
        var historicalObjects = targetStore.Snapshot()
            .Where(candidate => candidate.Key.Area == ArchiveArea.Hist)
            .ToArray();
        Assert.AreEqual(1, historicalObjects.Length);
        var historicalObject = historicalObjects[0];
        Assert.AreEqual("old content", ReadText(historicalObject.Content));
        Assert.IsTrue(
            historicalObject.Key.RelativePath.EndsWith("/folder/file.txt", StringComparison.Ordinal),
            $"Historical path was '{historicalObject.Key.RelativePath}'.");
    }

    [TestMethod]
    public async Task VerifyAsyncReturnsCompletedWhenLiveObjectsMatch()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var providers = serviceProvider.GetServices<IArchiveFormatProvider>().ToArray();
        Assert.AreEqual(1, providers.Length);
        var provider = providers[0];
        var sourceStore = new InMemoryObjectStore();
        var targetStore = new InMemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "same content");
        await UploadTextAsync(targetStore, "folder/file.txt", "same content");

        var result = await provider.VerifyAsync(CreateVerifyRequest(sourceStore, targetStore));

        Assert.IsTrue(result.Completed);
        StringAssert.Contains(result.Message, "verified 1 unchanged object(s)");
    }

    [TestMethod]
    public async Task VerifyAsyncReturnsIncompleteWhenLiveObjectsDiffer()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var providers = serviceProvider.GetServices<IArchiveFormatProvider>().ToArray();
        Assert.AreEqual(1, providers.Length);
        var provider = providers[0];
        var sourceStore = new InMemoryObjectStore();
        var targetStore = new InMemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");
        await UploadTextAsync(targetStore, "folder/file.txt", "target content");

        var result = await provider.VerifyAsync(CreateVerifyRequest(sourceStore, targetStore));

        Assert.IsFalse(result.Completed);
        StringAssert.Contains(result.Message, "1 changed object(s)");
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMirrorFormatProvider();

        return services;
    }

    private static ArchiveFormatBackupRequest CreateBackupRequest
    (
        IObjectStore sourceStore,
        IObjectStore targetStore
    )
    {
        return new
        (
            sourceStore,
            targetStore,
            CreateRoot("source-archive", "source"),
            CreateRoot("target-archive", "target"),
            new FolderPolicy(MirrorArchiveFormatName.Value)
        );
    }

    private static ArchiveFormatRestoreRequest CreateRestoreRequest
    (
        IObjectStore sourceStore,
        IObjectStore targetStore
    )
    {
        return new
        (
            sourceStore,
            targetStore,
            CreateRoot("source-archive", "source"),
            CreateRoot("target-archive", "target"),
            new FolderPolicy(MirrorArchiveFormatName.Value)
        );
    }

    private static ArchiveFormatVerifyRequest CreateVerifyRequest
    (
        IObjectStore sourceStore,
        IObjectStore targetStore
    )
    {
        return new
        (
            sourceStore,
            targetStore,
            CreateRoot("source-archive", "source"),
            CreateRoot("target-archive", "target"),
            new FolderPolicy(MirrorArchiveFormatName.Value)
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
        InMemoryObjectStore store,
        string relativePath,
        string content
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await store.UploadAsync(
            new(ArchiveArea.Live, relativePath),
            stream,
            "text/plain",
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static void AssertTextObject
    (
        InMemoryObjectStore store,
        ArchiveArea area,
        string relativePath,
        string expectedContent
    )
    {
        var archiveObject = store.GetObject(new(area, relativePath));
        Assert.AreEqual(expectedContent, ReadText(archiveObject.Content));
    }

    private static string ReadText(ReadOnlyMemory<byte> content)
    {
        return Encoding.UTF8.GetString(content.Span);
    }
}
