using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemObjectStoreTests
{
    [TestMethod]
    public async Task ListAsyncReturnsAllObjectsAcrossChunks()
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

            await foreach (var item in store.ListAsync(null))
            {
                keys.Add(item.Key);
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
    public async Task ListAsyncObservesCancellationAtNextChunkBoundary()
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
            await using var enumerator = store.ListAsync
            (
                null,
                cancellationSource.Token
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
