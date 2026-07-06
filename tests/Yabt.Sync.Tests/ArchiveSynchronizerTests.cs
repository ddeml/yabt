using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Format.Mirror;
using Yabt.Format.Zip;
using Yabt.Metadata;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ArchiveSynchronizerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    [TestMethod]
    public async Task SyncAsyncCopiesNewProjectedObjectToTarget()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "source content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesNestedFolderPolicy()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "readme.txt"),
                "root content");
            await WritePolicyAsync(
                photosRoot,
                ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(2, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "readme.txt"), "root content");
            Assert.IsFalse(File.Exists(Path.Combine(targetRoot, "photos", "image.txt")));

            var zipFiles = Directory.GetFiles(
                Path.Combine(targetRoot, "photos"),
                "photos.*.zip");
            Assert.AreEqual(1, zipFiles.Length);

            await using var package = File.OpenRead(zipFiles[0]);
            using var archive = new ZipArchive(package, ZipArchiveMode.Read);
            var entry = archive.GetEntry("image.txt");

            Assert.IsNotNull(entry);
            using var reader = new StreamReader(entry.Open());
            Assert.AreEqual("image content", await reader.ReadToEndAsync());
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUploadsProjectedObjectsBeforeSourceEnumerationCompletes()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-streaming-sync-test-{Guid.NewGuid():N}");
        var sourceStore = new FailingAfterFirstListedObjectStore();
        var targetStore = new MemoryObjectStore();
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var failedAfterFirstObject = false;
        try
        {
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
        }
        catch (InvalidOperationException)
        {
            failedAfterFirstObject = true;
        }

        Assert.IsTrue(failedAfterFirstObject);
        Assert.IsTrue(targetStore.TryGetObject("first.txt", out var uploadedObject));
        Assert.AreEqual(
            FailingAfterFirstListedObjectStore.FirstContent,
            Encoding.UTF8.GetString(uploadedObject.Content.Span));
    }

    [TestMethod]
    public async Task SyncAsyncMovesChangedTargetObjectToHistory()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "new content");
            await WriteTextFileAsync(
                Path.Combine(targetRoot, "folder", "file.txt"),
                "old content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.ChangedCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "new content");

            var historicalFiles = Directory.GetFiles(
                Path.Combine(targetRoot, ".yabt-hist"),
                "*",
                SearchOption.AllDirectories);
            Assert.AreEqual(1, historicalFiles.Length);
            AssertTextFile(historicalFiles[0], "old content");
            Assert.IsTrue(
                historicalFiles[0].EndsWith(
                    Path.Combine("folder", "file.txt"),
                    StringComparison.Ordinal),
                $"Historical path was '{historicalFiles[0]}'.");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncDoesNotCopySourceHistoryWhenLivePrefixIsEmpty()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, ".yabt-hist", "old.txt"),
                "historical content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(targetRoot, ".yabt-hist", "old.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncDiscoversRootDescriptorInBaseFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var sourceChildRoot = Path.Combine(sourceRoot, "folder");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceChildRoot, "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceChildRoot));

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(1, result.NewCount);
            AssertTextFile(Path.Combine(targetRoot, "file.txt"), "source content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesRequestedTargetStoreId()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var firstTargetRoot = Path.Combine(workspace, "target-first");
            var secondTargetRoot = Path.Combine(workspace, "target-second");
            await InitializeSourceRootAsync(
                sourceRoot,
                [
                    CreateFileSystemStore("first", firstTargetRoot),
                    CreateFileSystemStore("second", secondTargetRoot),
                ]);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(
                sourceRoot,
                TargetStoreId: "SECOND"));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(secondTargetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(firstTargetRoot, "folder", "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesDescriptorDefaultStoreId()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var firstTargetRoot = Path.Combine(workspace, "target-first");
            var secondTargetRoot = Path.Combine(workspace, "target-second");
            await InitializeSourceRootAsync(
                sourceRoot,
                [
                    CreateFileSystemStore("first", firstTargetRoot),
                    CreateFileSystemStore("second", secondTargetRoot),
                ],
                defaultStoreId: "second");
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(secondTargetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(firstTargetRoot, "folder", "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesFirstTargetStoreWhenNoStoreIdIsSelected()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var firstTargetRoot = Path.Combine(workspace, "target-first");
            var secondTargetRoot = Path.Combine(workspace, "target-second");
            await InitializeSourceRootAsync(
                sourceRoot,
                [
                    CreateFileSystemStore("first", firstTargetRoot),
                    CreateFileSystemStore("second", secondTargetRoot),
                ]);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "source content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            AssertTextFile(Path.Combine(firstTargetRoot, "folder", "file.txt"), "source content");
            Assert.IsFalse(File.Exists(Path.Combine(secondTargetRoot, "folder", "file.txt")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsDifferencesWithoutMutatingTarget()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(sourceRoot, "folder", "file.txt"),
                "new content");
            await WriteTextFileAsync(
                Path.Combine(targetRoot, "folder", "file.txt"),
                "old content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsFalse(result.Completed);
            Assert.AreEqual(1, result.ChangedCount);
            AssertTextFile(Path.Combine(targetRoot, "folder", "file.txt"), "old content");
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtFileSystemObjectStore();
        services.AddYabtMirrorFormatProjector();
        services.AddYabtZipFormatProjector();
        services.AddYabtMetadata();
        services.AddYabtSync();

        return services;
    }

    private static ServiceCollection CreateStreamingServices
    (
        string sourceRoot,
        BackupRootDescriptor descriptor,
        IObjectStore sourceStore,
        IObjectStore targetStore
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IBackupRootLocator>(new FixedBackupRootLocator(
            sourceRoot,
            descriptor));
        services.AddSingleton<IFolderPolicyReader, DefaultFolderPolicyReader>();
        services.AddSingleton<IBackupRootStoreResolver>(new FixedBackupRootStoreResolver(targetStore));
        services.AddSingleton<ISourceRootObjectStoreResolver>(new FixedSourceRootObjectStoreResolver(sourceStore));
        services.AddYabtMirrorFormatProjector();
        services.AddYabtSync();

        return services;
    }

    private static string CreateWorkspacePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            $"yabt-sync-tests-{Guid.NewGuid():N}");
    }

    private static Task InitializeSourceRootAsync
    (
        string sourceRoot,
        string targetRoot
    ) => InitializeSourceRootAsync(
        sourceRoot,
        [CreateFileSystemStore("target", targetRoot)]);

    private static async Task InitializeSourceRootAsync
    (
        string sourceRoot,
        IEnumerable<BackupRootStore> stores,
        string? defaultStoreId = default
    )
    {
        Directory.CreateDirectory(sourceRoot);

        var descriptor = CreateRootDescriptor(stores, defaultStoreId);
        await using var stream = File.Create(Path.Combine(sourceRoot, BackupRootFileNames.Primary));
        await JsonSerializer.SerializeAsync(
            stream,
            descriptor,
            JsonOptions);
    }

    private static async Task WritePolicyAsync
    (
        string folderPath,
        string format
    )
    {
        Directory.CreateDirectory(folderPath);

        await using var stream = File.Create(Path.Combine(folderPath, FolderPolicyFileNames.Primary));
        await JsonSerializer.SerializeAsync(
            stream,
            new FolderPolicy(format),
            JsonOptions);
    }

    private static BackupRootDescriptor CreateRootDescriptor
    (
        IEnumerable<BackupRootStore> stores,
        string? defaultStoreId = default
    )
    {
        return new
        (
            BackupRootDescriptor.ExpectedDocumentType,
            1,
            "source-archive",
            new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            stores,
            "source",
            DefaultStoreId: defaultStoreId
        );
    }

    private static BackupRootStore CreateFileSystemStore
    (
        string id,
        string rootPath
    )
    {
        Directory.CreateDirectory(rootPath);

        return new BackupRootStore(id, FileSystemObjectStoreKind.Value)
        {
            ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["rootPath"] = JsonSerializer.SerializeToElement(rootPath, JsonOptions),
            },
        };
    }

    private static async Task WriteTextFileAsync
    (
        string path,
        string content
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        await File.WriteAllTextAsync(path, content);
    }

    private static void AssertTextFile
    (
        string path,
        string expectedContent
    )
    {
        Assert.AreEqual(expectedContent, File.ReadAllText(path));
    }

    private static void DeleteWorkspace(string workspace)
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class FailingAfterFirstListedObjectStore : IObjectStore
    {
        public const string FirstContent = "first content";

        private static readonly byte[] FirstContentBytes = Encoding.UTF8.GetBytes(FirstContent);

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
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
            throw new NotSupportedException();
        }

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("first.txt", ArchiveLayout.NormalizeObjectKey(key));

            return Task.FromResult(new ArchiveObjectContent(
                new MemoryStream(FirstContentBytes, writable: false),
                "text/plain"));
        }

        public Task<bool> ExistsAsync
        (
            string key,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(string.Equals(
                ArchiveLayout.NormalizeObjectKey(key),
                "first.txt",
                StringComparison.Ordinal));
        }

        public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            _ = folderPrefix;
            _ = recursive;
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Yield();

            yield return ArchiveFolderItem.CreateObject(
                "first.txt",
                new
                (
                    "first.txt",
                    FirstContentBytes.Length
                ));

            throw new InvalidOperationException("Source enumeration failed after the first object.");
        }

        public Task MoveAsync
        (
            string source,
            string destination,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedBackupRootLocator
    (
        string _rootPath,
        BackupRootDescriptor _descriptor
    ) : IBackupRootLocator
    {
        public Task<BackupRootLocation> LocateRootAsync
        (
            string startPath,
            CancellationToken cancellationToken = default
        )
        {
            _ = startPath;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new BackupRootLocation(
                _rootPath,
                _descriptor));
        }
    }

    private sealed class DefaultFolderPolicyReader : IFolderPolicyReader
    {
        public Task<FolderPolicy> ReadPolicyAsync
        (
            string folderPath,
            CancellationToken cancellationToken = default
        )
        {
            _ = folderPath;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(FolderPolicy.Default);
        }
    }

    private sealed class FixedBackupRootStoreResolver(IObjectStore _targetStore) : IBackupRootStoreResolver
    {
        public const string StoreKindValue = "memory";

        public string StoreKind => StoreKindValue;

        public IObjectStore ResolveStore
        (
            BackupRootStore store,
            string descriptorRootPath
        )
        {
            _ = store;
            _ = descriptorRootPath;

            return _targetStore;
        }
    }

    private sealed class FixedSourceRootObjectStoreResolver(IObjectStore _sourceStore) : ISourceRootObjectStoreResolver
    {
        public IObjectStore ResolveSourceRoot(string rootPath)
        {
            _ = rootPath;

            return _sourceStore;
        }
    }
}
