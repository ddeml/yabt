using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Tests;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemContractTests
{
    [TestMethod]
    public Task FolderLocalTraversalPreservesExactNames() =>
        RunAsync(ObjectStoreContract.VerifyFolderLocalTraversalAsync);

    [TestMethod]
    public Task ConditionalMutationsPreserveMismatchedAndMissingObjects() =>
        RunAsync(store => ObjectStoreContract.VerifyConditionalMutationsAsync((IArchiveMutableObjectStore)store));

    [TestMethod]
    public Task WholeFolderMovePreservesMarkersAndSiblingPrefixes() =>
        RunAsync(ObjectStoreContract.VerifyWholeFolderMoveAsync);

    [TestMethod]
    public Task UploadIsCreateIfAbsent() => RunAsync(ObjectStoreContract.VerifyCreateIfAbsentAsync);

    [TestMethod]
    public Task FailedSourceReadLeavesNoPartialFinalObject() =>
        RunAsync(ObjectStoreContract.VerifyFailedUploadAsync);

    [TestMethod]
    public Task ArchiveLockSerializesOwnersAndCanBeReacquired() =>
        RunAsync(store => ObjectStoreContract.VerifyLockContentionAndReacquisitionAsync((IArchiveMutableObjectStore)store));

    private static async Task RunAsync(Func<IObjectStore, Task> contract)
    {
        var root = Path.Combine(Path.GetTempPath(), "Yabt.FileSystem.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddSingleton<IOptionsMonitor<FileSystemObjectStoreOptions>>(new ContractOptionsMonitor(root));
            using var provider = services.BuildServiceProvider();
            await contract(provider.GetRequiredService<IObjectStore>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ContractOptionsMonitor(string root) : IOptionsMonitor<FileSystemObjectStoreOptions>
    {
        public FileSystemObjectStoreOptions CurrentValue { get; } = new() { RootPath = root };

        public FileSystemObjectStoreOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<FileSystemObjectStoreOptions, string?> listener) => null;
    }
}
