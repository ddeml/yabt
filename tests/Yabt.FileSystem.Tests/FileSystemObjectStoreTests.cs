using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemObjectStoreTests
{
    [TestMethod]
    public async Task UploadAsyncLeavesSharedTemporaryDirectoryEmpty()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();
            await using var content = new MemoryStream("content"u8.ToArray());

            await store.UploadAsync
            (
                "folder/file.txt",
                content,
                "text/plain",
                new Dictionary<string, string>(StringComparer.Ordinal)
            );

            Assert.IsTrue(File.Exists(Path.Combine(rootPath, "folder", "file.txt")));
            var temporaryDirectory = Path.Combine(rootPath, ".yabt-tmp");
            Assert.IsTrue(Directory.Exists(temporaryDirectory));
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(temporaryDirectory).Any());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFolderItemsAsyncReturnsAllObjectsAcrossChunks()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            await WriteFileAsync(rootPath, "one.txt");
            await WriteFileAsync(rootPath, "folder/two.txt");
            await WriteFileAsync(rootPath, "folder/three.txt");
            await WriteFileAsync(rootPath, "folder/deeper/four.txt");
            await WriteFileAsync(rootPath, "folder/deeper/five.txt");
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 2).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();
            var keys = new List<string>();
            var folderItems = store.GetFolderItemsAsync(
                null,
                recursive: true);

            await foreach (var folderItem in folderItems)
            {
                Assert.IsNotNull(folderItem.Object);
                keys.Add(folderItem.Object.Key);
            }

            CollectionAssert.AreEquivalent
            (
                new[]
                {
                    "one.txt",
                    "folder/two.txt",
                    "folder/three.txt",
                    "folder/deeper/four.txt",
                    "folder/deeper/five.txt",
                },
                keys
            );
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFolderItemsAsyncObservesCancellationAtNextChunkBoundary()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            await WriteFileAsync(rootPath, "one.txt");
            await WriteFileAsync(rootPath, "two.txt");
            await WriteFileAsync(rootPath, "three.txt");
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 3).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();
            using var cancellationSource = new CancellationTokenSource();
            await using var enumerator = store.GetFolderItemsAsync
            (
                null,
                cancellationToken: cancellationSource.Token
            ).GetAsyncEnumerator();

            Assert.IsTrue(await enumerator.MoveNextAsync());
            await cancellationSource.CancelAsync();

            Assert.IsTrue(await enumerator.MoveNextAsync());
            Assert.IsTrue(await enumerator.MoveNextAsync());
            await Assert.ThrowsExactlyAsync<OperationCanceledException>
            (
                () => enumerator.MoveNextAsync().AsTask()
            );
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetFolderItemsAsyncDoesNotTraverseDirectoryReparsePoints()
    {
        var rootPath = CreateTemporaryRoot();
        var outsidePath = CreateTemporaryRoot();
        var linkPath = Path.Combine(rootPath, "linked-history");
        try
        {
            await WriteFileAsync(outsidePath, "outside.txt");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsidePath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                PlatformNotSupportedException or IOException)
            {
                Assert.Inconclusive($"Directory symbolic links are unavailable: {ex.Message}");
            }

            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();
            var keys = new List<string>();
            var folderItems = store.GetFolderItemsAsync(null, recursive: true);

            await foreach (var folderItem in folderItems)
            {
                if (folderItem.Object is not null)
                {
                    keys.Add(folderItem.Object.Key);
                }
            }

            Assert.AreEqual(0, keys.Count);
            Assert.IsTrue(File.Exists(Path.Combine(outsidePath, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            Directory.Delete(rootPath, recursive: true);
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [TestMethod]
    public async Task MoveFolderAsyncMovesCompleteDirectoryTree()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            await WriteFileAsync(rootPath, "source/file.txt");
            await WriteFileAsync(
                rootPath,
                $"source/empty/{ArchiveFolderMarkerFileNames.EmptyFolder}");
            Directory.CreateDirectory(Path.Combine(rootPath, "source", "native-empty"));
            await WriteFileAsync(rootPath, "source-other/keep.txt");
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();

            await store.MoveFolderAsync(
                "source",
                "history/stamp/source");

            Assert.IsFalse(Directory.Exists(Path.Combine(rootPath, "source")));
            Assert.IsTrue(File.Exists(Path.Combine(rootPath, "history", "stamp", "source", "file.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(
                rootPath,
                "history",
                "stamp",
                "source",
                "empty",
                ArchiveFolderMarkerFileNames.EmptyFolder)));
            Assert.IsTrue(Directory.Exists(Path.Combine(
                rootPath,
                "history",
                "stamp",
                "source",
                "native-empty")));
            Assert.IsTrue(File.Exists(Path.Combine(rootPath, "source-other", "keep.txt")));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task MoveFolderAsyncDoesNotOverwriteDestination()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            await WriteFileAsync(rootPath, "source/file.txt");
            await WriteFileAsync(rootPath, "history/stamp/source/existing.txt");
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();

            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(
                () => store.MoveFolderAsync(
                    "source",
                    "history/stamp/source"));

            Assert.IsTrue(File.Exists(Path.Combine(rootPath, "source", "file.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(
                rootPath,
                "history",
                "stamp",
                "source",
                "existing.txt")));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task MoveFolderAsyncRejectsDestinationInsideSource()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            await WriteFileAsync(rootPath, "source/file.txt");
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();

            await Assert.ThrowsExactlyAsync<YabtFileSystemException>(
                () => store.MoveFolderAsync(
                    "source",
                    "source/history"));

            Assert.IsTrue(File.Exists(Path.Combine(rootPath, "source", "file.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(rootPath, "source", "history")));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConditionalReplacementRequiresMatchingCompleteContentHash()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            var path = Path.Combine(rootPath, "file.txt");
            await File.WriteAllTextAsync(path, "original");
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();
            var mutableStore = (IArchiveMutableObjectStore)store;
            await using var wrongReplacement = new MemoryStream("wrong"u8.ToArray());

            var wrongHashReplaced = await mutableStore.TryReplaceIfContentHashMatchesAsync
            (
                "file.txt",
                ArchiveHash.Compute("different"u8),
                wrongReplacement,
                "text/plain",
                new Dictionary<string, string>(StringComparer.Ordinal)
            );

            Assert.IsFalse(wrongHashReplaced);
            Assert.AreEqual("original", await File.ReadAllTextAsync(path));

            await using var replacement = new MemoryStream("replacement"u8.ToArray());
            var replaced = await mutableStore.TryReplaceIfContentHashMatchesAsync
            (
                "file.txt",
                ArchiveHash.Compute("original"u8),
                replacement,
                "text/plain",
                new Dictionary<string, string>(StringComparer.Ordinal)
            );

            Assert.IsTrue(replaced);
            Assert.AreEqual("replacement", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConditionalDeletionRequiresMatchingCompleteContentHash()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            var path = Path.Combine(rootPath, "file.txt");
            await File.WriteAllTextAsync(path, "content");
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();
            var mutableStore = (IArchiveMutableObjectStore)store;

            var wrongHashDeleted = await mutableStore.TryDeleteIfContentHashMatchesAsync
            (
                "file.txt",
                ArchiveHash.Compute("different"u8)
            );

            Assert.IsFalse(wrongHashDeleted);
            Assert.IsTrue(File.Exists(path));

            var deleted = await mutableStore.TryDeleteIfContentHashMatchesAsync
            (
                "file.txt",
                ArchiveHash.Compute("content"u8)
            );

            Assert.IsTrue(deleted);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task ArchiveMutationLockIsExclusiveAndRemovedOnRelease()
    {
        var rootPath = CreateTemporaryRoot();
        try
        {
            using var serviceProvider = CreateServices(rootPath, listChunkSize: 10).BuildServiceProvider();
            var store = serviceProvider.GetRequiredService<IObjectStore>();
            var mutableStore = (IArchiveMutableObjectStore)store;
            var mutationLock = await mutableStore.AcquireArchiveMutationLockAsync();

            try
            {
                Assert.IsTrue(File.Exists(Path.Combine
                (
                    rootPath,
                    ArchiveInternalObjectKeys.MutationLock.Replace('/', Path.DirectorySeparatorChar)
                )));

                using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAsync<OperationCanceledException>
                (
                    () => mutableStore.AcquireArchiveMutationLockAsync(cancellationSource.Token)
                );
            }
            finally
            {
                await mutationLock.DisposeAsync();
            }

            Assert.IsFalse(File.Exists(Path.Combine
            (
                rootPath,
                ArchiveInternalObjectKeys.MutationLock.Replace('/', Path.DirectorySeparatorChar)
            )));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static ServiceCollection CreateServices(string rootPath, int listChunkSize)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        services.AddSingleton<IOptionsMonitor<FileSystemObjectStoreOptions>>
        (
            new StaticOptionsMonitor<FileSystemObjectStoreOptions>
            (
                new()
                {
                    RootPath = rootPath,
                    ListChunkSize = listChunkSize,
                }
            )
        );
        return services;
    }

    private static string CreateTemporaryRoot()
    {
        var rootPath = Path.Combine
        (
            Path.GetTempPath(),
            "Yabt.FileSystem.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    private static async Task WriteFileAsync(string rootPath, string relativePath)
    {
        var path = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, relativePath);
    }

    private sealed class StaticOptionsMonitor<T>(T _value) : IOptionsMonitor<T>
    {
        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
