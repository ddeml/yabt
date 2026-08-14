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
    public async Task SyncAsyncPlacesNestedZipPackageInParentTargetFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
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
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));

            var zipFiles = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
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
    public async Task SyncAsyncPreservesNestedMirrorFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(
                photosRoot,
                MirrorArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            using var serviceProvider = CreateServices().BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncUsesZipPolicyNestedWithinMirrorPolicy()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var albumsRoot = Path.Combine(sourceRoot, "albums");
            var photosRoot = Path.Combine(albumsRoot, "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(
                albumsRoot,
                MirrorArchiveFormatName.Value);
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
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            var zipFiles = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip");
            Assert.AreEqual(1, zipFiles.Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncMovesMirroredFolderToHistoryWhenPolicyChangesToZip()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
            await WriteTextFileAsync(
                Path.Combine(targetRoot, "albums", "photos", "orphan.txt"),
                "unexpected target content");
            Directory.CreateDirectory(Path.Combine(
                targetRoot,
                "albums",
                "photos",
                "native-empty"));

            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);

            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(zipResult.Completed);
            Assert.AreEqual(1, zipResult.NewCount);
            Assert.AreEqual(2, zipResult.ExtraCount);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            Assert.AreEqual(
                1,
                Directory.GetFiles(Path.Combine(targetRoot, "albums"), "photos.*.zip").Length);
            var historicalImages = Directory.GetFiles(
                Path.Combine(targetRoot, ".yabt-hist"),
                "image.txt",
                SearchOption.AllDirectories);
            Assert.AreEqual(1, historicalImages.Length);
            AssertTextFile(historicalImages[0], "image content");
            var historicalPhotosRoot = Path.GetDirectoryName(historicalImages[0]);
            Assert.IsNotNull(historicalPhotosRoot);
            AssertTextFile(
                Path.Combine(historicalPhotosRoot, "orphan.txt"),
                "unexpected target content");
            Assert.IsTrue(Directory.Exists(Path.Combine(
                historicalPhotosRoot,
                "native-empty")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncPreservesEmptyFolderMarkerWhenMirrorChangesToZip()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            Directory.CreateDirectory(photosRoot);

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            Assert.IsTrue(File.Exists(Path.Combine(
                targetRoot,
                "albums",
                "photos",
                ArchiveFolderMarkerFileNames.EmptyFolder)));

            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);

            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(zipResult.Completed);
            Assert.AreEqual(1, zipResult.NewCount);
            Assert.AreEqual(1, zipResult.ExtraCount);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            var historicalMarkers = Directory.GetFiles(
                Path.Combine(targetRoot, ".yabt-hist"),
                ArchiveFolderMarkerFileNames.EmptyFolder,
                SearchOption.AllDirectories);
            Assert.AreEqual(1, historicalMarkers.Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncRemovesEmptyFolderMarkerWhenMirrorFolderBecomesNonempty()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            Directory.CreateDirectory(photosRoot);

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var emptyResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(emptyResult.Completed);

            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var nonemptyResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(nonemptyResult.Completed);
            Assert.AreEqual(1, nonemptyResult.NewCount);
            Assert.AreEqual(1, nonemptyResult.ExtraCount);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                "albums",
                "photos",
                ArchiveFolderMarkerFileNames.EmptyFolder)));
            Assert.AreEqual(
                1,
                Directory.GetFiles(
                    Path.Combine(targetRoot, ".yabt-hist"),
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncMovesSelectedRootMarkerToHistoryWhenRootChangesToZip()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            Assert.IsTrue(File.Exists(Path.Combine(
                targetRoot,
                ArchiveFolderMarkerFileNames.EmptyFolder)));

            await WritePolicyAsync(sourceRoot, ZipArchiveFormatName.Value);

            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(zipResult.Completed);
            Assert.AreEqual(1, zipResult.NewCount);
            Assert.AreEqual(1, zipResult.ExtraCount);
            Assert.IsFalse(File.Exists(Path.Combine(
                targetRoot,
                ArchiveFolderMarkerFileNames.EmptyFolder)));
            Assert.AreEqual(1, Directory.GetFiles(targetRoot, "source.*.zip").Length);
            Assert.AreEqual(
                1,
                Directory.GetFiles(
                    Path.Combine(targetRoot, ".yabt-hist"),
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsStaleEmptyFolder()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var syncResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(syncResult.Completed);
            Directory.CreateDirectory(Path.Combine(targetRoot, "albums", "photos"));

            var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsFalse(verifyResult.Completed);
            Assert.AreEqual(0, verifyResult.NewCount);
            Assert.AreEqual(0, verifyResult.ChangedCount);
            Assert.AreEqual(1, verifyResult.ExtraCount);
            Assert.AreEqual(1, verifyResult.UnchangedCount);
            Assert.IsTrue(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, ".yabt-hist")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncPreservesDesiredAncestorsWhenZipChangesToMirror()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();
            var zipResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            Assert.IsTrue(zipResult.Completed);

            await WritePolicyAsync(photosRoot, MirrorArchiveFormatName.Value);

            var mirrorResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(mirrorResult.Completed);
            Assert.AreEqual(2, mirrorResult.NewCount);
            Assert.AreEqual(1, mirrorResult.ExtraCount);
            AssertTextFile(
                Path.Combine(targetRoot, "albums", "photos", "image.txt"),
                "image content");
            Assert.AreEqual(
                0,
                Directory.GetFiles(Path.Combine(targetRoot, "albums"), "photos.*.zip").Length);
            Assert.AreEqual(
                1,
                Directory.GetFiles(
                    Path.Combine(targetRoot, ".yabt-hist"),
                    "photos.*.zip",
                    SearchOption.AllDirectories).Length);

            var verifyResult = await synchronizer.VerifyAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(verifyResult.Completed);
            Assert.AreEqual(2, verifyResult.UnchangedCount);
            Assert.AreEqual(0, verifyResult.ExtraCount);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncAllocatesNewHistoryFolderForRepeatedSameTimeTransition()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var photosRoot = Path.Combine(sourceRoot, "albums", "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            await WritePolicyAsync(photosRoot, MirrorArchiveFormatName.Value);
            await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));
            await WritePolicyAsync(photosRoot, ZipArchiveFormatName.Value);

            var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(result.Completed);
            Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "albums", "photos")));
            var historicalPhotoFolders = Directory.GetDirectories(
                Path.Combine(targetRoot, ".yabt-hist"),
                "photos",
                SearchOption.AllDirectories);
            Assert.AreEqual(2, historicalPhotoFolders.Length);
            var historicalRootNames = historicalPhotoFolders
                .Select(path => Directory.GetParent(path)?.Parent?.Name)
                .ToArray();
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "20260724T120000Z",
                    "20260724T120000Z-1",
                },
                historicalRootNames);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task SyncAsyncRejectsZipPackageNameCollisionWithSourceObject()
    {
        await AssertZipPackageNameCollisionRejectedAsync(
            createSourceFolderCollision: false);
    }

    [TestMethod]
    public async Task SyncAsyncRejectsZipPackageNameCollisionWithSourceFolder()
    {
        await AssertZipPackageNameCollisionRejectedAsync(
            createSourceFolderCollision: true);
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
    public async Task SyncAsyncHistorizesExactObjectAndSameNamedFolderSeparately()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"yabt-object-folder-collision-test-{Guid.NewGuid():N}");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var sourceStore = new MemoryObjectStore(timeProvider);
        var targetStore = new MemoryObjectStore(timeProvider);
        await UploadTextObjectAsync(sourceStore, "a/current.txt", "current content");
        await UploadTextObjectAsync(targetStore, "a/b", "object content");
        await UploadTextObjectAsync(targetStore, "a/b/file.txt", "folder content");
        var descriptor = CreateRootDescriptor(
            [new BackupRootStore("target", FixedBackupRootStoreResolver.StoreKindValue)]);

        using var serviceProvider = CreateStreamingServices(
            sourceRoot,
            descriptor,
            sourceStore,
            targetStore,
            timeProvider).BuildServiceProvider();
        var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

        var result = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, result.NewCount);
        Assert.AreEqual(2, result.ExtraCount);
        Assert.IsTrue(targetStore.TryGetObject("a/current.txt", out _));
        Assert.IsFalse(targetStore.TryGetObject("a/b", out _));
        Assert.IsFalse(targetStore.TryGetObject("a/b/file.txt", out _));
        Assert.IsTrue(targetStore.TryGetObject(
            ".yabt-hist/20260724T120000Z/a/b",
            out var historicalObject));
        Assert.AreEqual(
            "object content",
            Encoding.UTF8.GetString(historicalObject.Content.Span));
        Assert.IsTrue(targetStore.TryGetObject(
            ".yabt-hist/20260724T120000Z-1/a/b/file.txt",
            out var historicalFolderObject));
        Assert.AreEqual(
            "folder content",
            Encoding.UTF8.GetString(historicalFolderObject.Content.Span));
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

    private static async Task AssertZipPackageNameCollisionRejectedAsync
    (
        bool createSourceFolderCollision
    )
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var targetRoot = Path.Combine(workspace, "target");
            var albumsRoot = Path.Combine(sourceRoot, "albums");
            var photosRoot = Path.Combine(albumsRoot, "photos");
            await InitializeSourceRootAsync(sourceRoot, targetRoot);
            await WritePolicyAsync(
                photosRoot,
                ZipArchiveFormatName.Value);
            await WriteTextFileAsync(
                Path.Combine(photosRoot, "image.txt"),
                "image content");

            var timeProvider = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
            using var serviceProvider = CreateServices(timeProvider).BuildServiceProvider();
            var synchronizer = serviceProvider.GetRequiredService<IArchiveSynchronizer>();

            var firstResult = await synchronizer.SyncAsync(new SyncRunRequest(sourceRoot));

            Assert.IsTrue(firstResult.Completed);
            var packagePath = Directory.GetFiles(
                Path.Combine(targetRoot, "albums"),
                "photos.*.zip").Single();
            var collisionPath = Path.Combine(
                albumsRoot,
                Path.GetFileName(packagePath));
            if (createSourceFolderCollision)
            {
                await WriteTextFileAsync(
                    Path.Combine(collisionPath, "file.txt"),
                    "ordinary sibling content");
            }
            else
            {
                await WriteTextFileAsync(
                    collisionPath,
                    "ordinary sibling content");
            }

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => synchronizer.SyncAsync(new SyncRunRequest(sourceRoot)));

            StringAssert.Contains(exception.Message, "conflicts with a source item");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    private static ServiceCollection CreateServices(TimeProvider? timeProvider = default)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

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
        IObjectStore targetStore,
        TimeProvider? timeProvider = default
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

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

    private static async Task UploadTextObjectAsync
    (
        MemoryObjectStore store,
        string key,
        string content
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        await store.UploadAsync(
            key,
            stream,
            "text/plain",
            new Dictionary<string, string>(StringComparer.Ordinal));
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

        public Task MoveFolderAsync
        (
            string sourcePrefix,
            string destinationPrefix,
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

    private sealed class FixedTimeProvider(DateTimeOffset _utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
