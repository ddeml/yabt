using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RestorePathVerifierTests
{
    private const int ComparisonBufferSize = 81_920;

    [TestMethod]
    public async Task VerifyAsyncAcceptsIdenticalTreesAndIgnoresRootRestoreMetadata()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            var binaryContent = Enumerable.Range(0, 257)
                .Select(index => checked((byte)(index % 251)))
                .ToArray();

            await WriteMatchingFileAsync(
                sourceRoot,
                restoreRoot,
                BackupRootFileNames.Primary,
                "root descriptor"u8.ToArray());
            await WriteMatchingFileAsync(
                sourceRoot,
                restoreRoot,
                "folder/file.txt",
                "nested text"u8.ToArray());
            await WriteMatchingFileAsync(
                sourceRoot,
                restoreRoot,
                "folder/data.bin",
                binaryContent);
            Directory.CreateDirectory(Path.Combine(sourceRoot, "empty"));
            Directory.CreateDirectory(Path.Combine(restoreRoot, "empty"));

            await WriteFileAsync(
                Path.Combine(restoreRoot, ArchiveLogicalStateManifest.FileName),
                "restore manifest"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(
                    restoreRoot,
                    ArchiveInternalFolderNames.TemporaryUploads,
                    "restore-only.tmp"),
                [4, 5, 6]);

            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var result = await verifier.VerifyAsync(new(sourceRoot, restoreRoot));

            Assert.IsTrue(result.Identical);
            Assert.AreEqual(0, result.SourceOnlyCount);
            Assert.AreEqual(0, result.DifferentCount);
            Assert.AreEqual(0, result.RestoreOnlyCount);
            Assert.AreEqual(3, result.ComparedFileCount);
            Assert.AreEqual(2, result.ComparedDirectoryCount);
            StringAssert.Contains(result.Message, "completed byte-for-byte");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncDoesNotExcludeRootLogicalStateInvalidationMarker()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            Directory.CreateDirectory(sourceRoot);
            await WriteFileAsync(
                Path.Combine(
                    restoreRoot,
                    ArchiveLogicalStateManifest.InvalidationMarkerFileName),
                "unfinished restore"u8.ToArray());

            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var result = await verifier.VerifyAsync(new(sourceRoot, restoreRoot));

            Assert.IsFalse(result.Identical);
            Assert.AreEqual(0, result.SourceOnlyCount);
            Assert.AreEqual(0, result.DifferentCount);
            Assert.AreEqual(1, result.RestoreOnlyCount);
            Assert.AreEqual(0, result.ComparedFileCount);
            Assert.AreEqual(0, result.ComparedDirectoryCount);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncDetectsSameLengthChangesAtAndBeyondComparisonBufferBoundary()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            var sourceContent = new byte[ComparisonBufferSize * 3 + 1];
            for (var index = 0; index < sourceContent.Length; index++)
            {
                sourceContent[index] = checked((byte)(index % 251));
            }

            var restoreContent = sourceContent.ToArray();
            restoreContent[ComparisonBufferSize] ^= 0xff;
            restoreContent[^1] ^= 0xff;
            var sourcePath = Path.Combine(sourceRoot, "large.bin");
            var restorePath = Path.Combine(restoreRoot, "large.bin");
            await WriteFileAsync(sourcePath, sourceContent);
            await WriteFileAsync(restorePath, restoreContent);
            var preservedTimestamp = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(sourcePath, preservedTimestamp);
            File.SetLastWriteTimeUtc(restorePath, preservedTimestamp);

            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var result = await verifier.VerifyAsync(new(sourceRoot, restoreRoot));

            Assert.IsFalse(result.Identical);
            Assert.AreEqual(0, result.SourceOnlyCount);
            Assert.AreEqual(1, result.DifferentCount);
            Assert.AreEqual(0, result.RestoreOnlyCount);
            Assert.AreEqual(1, result.ComparedFileCount);
            Assert.AreEqual(0, result.ComparedDirectoryCount);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsAllDifferenceKindsWithoutMutatingEitherTree()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            await WriteMatchingFileAsync(
                sourceRoot,
                restoreRoot,
                "same.txt",
                "same"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(sourceRoot, "source-only.txt"),
                "source only"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(restoreRoot, "restore-only.txt"),
                "restore only"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(sourceRoot, "file-to-directory"),
                "source file"u8.ToArray());
            Directory.CreateDirectory(Path.Combine(restoreRoot, "file-to-directory"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, "directory-to-file"));
            await WriteFileAsync(
                Path.Combine(restoreRoot, "directory-to-file"),
                "restore file"u8.ToArray());
            Directory.CreateDirectory(Path.Combine(sourceRoot, "source-only-empty"));
            Directory.CreateDirectory(Path.Combine(restoreRoot, "restore-only-empty"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, "matching-empty"));
            Directory.CreateDirectory(Path.Combine(restoreRoot, "matching-empty"));
            var sourceSnapshot = CaptureTree(sourceRoot);
            var restoreSnapshot = CaptureTree(restoreRoot);

            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var result = await verifier.VerifyAsync(new(sourceRoot, restoreRoot));

            Assert.IsFalse(result.Identical);
            Assert.AreEqual(2, result.SourceOnlyCount);
            Assert.AreEqual(2, result.DifferentCount);
            Assert.AreEqual(2, result.RestoreOnlyCount);
            Assert.AreEqual(1, result.ComparedFileCount);
            Assert.AreEqual(1, result.ComparedDirectoryCount);
            CollectionAssert.AreEqual(sourceSnapshot, CaptureTree(sourceRoot));
            CollectionAssert.AreEqual(restoreSnapshot, CaptureTree(restoreRoot));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncAppliesRestoreMetadataExclusionsOnlyAtTheRoot()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            await WriteFileAsync(
                Path.Combine(sourceRoot, ArchiveLogicalStateManifest.FileName),
                "ignored source manifest"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(restoreRoot, ArchiveLogicalStateManifest.FileName),
                "ignored restore manifest"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(
                    sourceRoot,
                    ArchiveInternalFolderNames.TemporaryUploads,
                    "ignored-source.tmp"),
                [1]);
            await WriteFileAsync(
                Path.Combine(
                    restoreRoot,
                    ArchiveInternalFolderNames.TemporaryUploads,
                    "ignored-restore.tmp"),
                [2]);

            await WriteFileAsync(
                Path.Combine(
                    sourceRoot,
                    "child",
                    ArchiveLogicalStateManifest.FileName),
                "nested source manifest"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(
                    restoreRoot,
                    "child",
                    ArchiveLogicalStateManifest.FileName),
                "nested restore manifest"u8.ToArray());
            await WriteFileAsync(
                Path.Combine(
                    sourceRoot,
                    "child",
                    ArchiveInternalFolderNames.TemporaryUploads,
                    "source-only.tmp"),
                [3]);
            await WriteFileAsync(
                Path.Combine(
                    restoreRoot,
                    "child",
                    ArchiveInternalFolderNames.TemporaryUploads,
                    "restore-only.tmp"),
                [4]);

            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var result = await verifier.VerifyAsync(new(sourceRoot, restoreRoot));

            Assert.IsFalse(result.Identical);
            Assert.AreEqual(1, result.SourceOnlyCount);
            Assert.AreEqual(1, result.DifferentCount);
            Assert.AreEqual(1, result.RestoreOnlyCount);
            Assert.AreEqual(1, result.ComparedFileCount);
            Assert.AreEqual(2, result.ComparedDirectoryCount);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncRejectsMissingRoots()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            Directory.CreateDirectory(restoreRoot);
            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var sourceException = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => verifier.VerifyAsync(new(sourceRoot, restoreRoot)));

            StringAssert.Contains(sourceException.Message, "source path");
            StringAssert.Contains(sourceException.Message, "does not exist");

            Directory.CreateDirectory(sourceRoot);
            Directory.Delete(restoreRoot);
            var restoreException = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => verifier.VerifyAsync(new(sourceRoot, restoreRoot)));

            StringAssert.Contains(restoreException.Message, "restore path");
            StringAssert.Contains(restoreException.Message, "does not exist");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncRejectsFileRoots()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            await WriteFileAsync(sourceRoot, [1]);
            Directory.CreateDirectory(restoreRoot);
            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var sourceException = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => verifier.VerifyAsync(new(sourceRoot, restoreRoot)));

            StringAssert.Contains(sourceException.Message, "source path");
            StringAssert.Contains(sourceException.Message, "is a file, not a folder");

            File.Delete(sourceRoot);
            Directory.CreateDirectory(sourceRoot);
            Directory.Delete(restoreRoot);
            await WriteFileAsync(restoreRoot, [2]);
            var restoreException = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => verifier.VerifyAsync(new(sourceRoot, restoreRoot)));

            StringAssert.Contains(restoreException.Message, "restore path");
            StringAssert.Contains(restoreException.Message, "is a file, not a folder");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsUnderlyingPathFailureDetails()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var restoreRoot = Path.Combine(workspace, "restore");
            Directory.CreateDirectory(restoreRoot);
            var invalidSourceRoot = $"invalid{Path.GetInvalidPathChars()[0]}path";
            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var exception = await Assert.ThrowsExactlyAsync<YabtSyncException>(
                () => verifier.VerifyAsync(new(invalidSourceRoot, restoreRoot)));

            Assert.IsNotNull(exception.InnerException);
            StringAssert.Contains(
                exception.Message,
                "source path",
                StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(
                exception.Message,
                exception.InnerException.Message);
            Assert.AreNotEqual(
                "Restore path verification could not safely compare the source and restore paths.",
                exception.Message);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsTheFilePairWhenContentCannotBeRead()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            const string relativePath = "locked.bin";
            await WriteMatchingFileAsync(
                sourceRoot,
                restoreRoot,
                relativePath,
                [1, 2, 3]);
            var sourcePath = Path.Combine(sourceRoot, relativePath);
            var restorePath = Path.Combine(restoreRoot, relativePath);
            await using var lockedRestoreFile = new FileStream(
                restorePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var expectedReason = CaptureReadFailureReason(restorePath);
            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var result = await verifier.VerifyAsync(new(sourceRoot, restoreRoot));

            Assert.IsFalse(result.Identical);
            Assert.AreEqual(1, result.UncomparedItemCount);
            Assert.AreEqual(0, result.DifferentCount);
            StringAssert.Contains(result.Message, relativePath);
            StringAssert.Contains(result.Message, sourcePath);
            StringAssert.Contains(result.Message, restorePath);
            StringAssert.Contains(result.Message, "could not open restore file");
            StringAssert.Contains(result.Message, expectedReason);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncReportsMultipleUnreadableFilesAndContinuesComparing()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            const string lockedSourceRelativePath = "a-locked-source.bin";
            const string lockedRestoreRelativePath = "b-locked-restore.bin";
            const string differentRelativePath = "z-different.bin";
            await WriteMatchingFileAsync(
                sourceRoot,
                restoreRoot,
                lockedSourceRelativePath,
                [1, 2, 3]);
            await WriteMatchingFileAsync(
                sourceRoot,
                restoreRoot,
                lockedRestoreRelativePath,
                [4, 5, 6]);
            await WriteFileAsync(
                Path.Combine(sourceRoot, differentRelativePath),
                [7, 8, 9]);
            await WriteFileAsync(
                Path.Combine(restoreRoot, differentRelativePath),
                [7, 8, 0]);
            var lockedSourcePath = Path.Combine(sourceRoot, lockedSourceRelativePath);
            var lockedRestorePath = Path.Combine(restoreRoot, lockedRestoreRelativePath);
            await using var lockedSourceFile = new FileStream(
                lockedSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            await using var lockedRestoreFile = new FileStream(
                lockedRestorePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();

            var result = await verifier.VerifyAsync(new(sourceRoot, restoreRoot));

            Assert.IsFalse(result.Identical);
            Assert.AreEqual(2, result.UncomparedItemCount);
            Assert.AreEqual(1, result.DifferentCount);
            Assert.AreEqual(3, result.ComparedFileCount);
            StringAssert.Contains(result.Message, lockedSourceRelativePath);
            StringAssert.Contains(result.Message, "could not open source file");
            StringAssert.Contains(result.Message, lockedRestoreRelativePath);
            StringAssert.Contains(result.Message, "could not open restore file");
            StringAssert.Contains(result.Message, "Items that could not be compared:");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [TestMethod]
    public async Task VerifyAsyncHonorsCancellation()
    {
        var workspace = CreateWorkspacePath();
        try
        {
            var sourceRoot = Path.Combine(workspace, "source");
            var restoreRoot = Path.Combine(workspace, "restore");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(restoreRoot);
            using var serviceProvider = CreateServiceProvider();
            var verifier = serviceProvider.GetRequiredService<IRestorePathVerifier>();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => verifier.VerifyAsync(
                    new(sourceRoot, restoreRoot),
                    cancellation.Token));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtSync();
        return services.BuildServiceProvider();
    }

    private static async Task WriteMatchingFileAsync
    (
        string sourceRoot,
        string restoreRoot,
        string relativePath,
        byte[] content
    )
    {
        var fileSystemRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        await WriteFileAsync(Path.Combine(sourceRoot, fileSystemRelativePath), content);
        await WriteFileAsync(Path.Combine(restoreRoot, fileSystemRelativePath), content);
    }

    private static async Task WriteFileAsync(string path, byte[] content)
    {
        var parentPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parentPath))
        {
            Directory.CreateDirectory(parentPath);
        }

        await File.WriteAllBytesAsync(path, content);
    }

    private static string[] CaptureTree(string rootPath)
    {
        var directories = Directory
            .EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
            .Select(path => $"D:{ToRelativePath(rootPath, path)}");
        var files = Directory
            .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Select(path =>
                $"F:{ToRelativePath(rootPath, path)}:{Convert.ToHexString(File.ReadAllBytes(path))}");
        return directories
            .Concat(files)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string CaptureReadFailureReason(string path)
    {
        try
        {
            using var unexpectedlyReadableFile = File.OpenRead(path);
            Assert.Fail($"Expected '{path}' to be unreadable while exclusively locked.");
            return string.Empty;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    private static string ToRelativePath(string rootPath, string path) =>
        Path.GetRelativePath(rootPath, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static string CreateWorkspacePath() => Path.Combine(
        Path.GetTempPath(),
        $"yabt-restore-path-verifier-tests-{Guid.NewGuid():N}");

    private static void DeleteWorkspace(string workspace)
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
