using System.IO.Compression;
using System.Text;
using System.Text.Json;
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
public sealed class PathIdentityTests
{
    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value)]
    [DataRow(ZipArchiveFormatName.Value)]
    public async Task BackupVerifyAndRestorePreserveDistinctWhitespacePathIdentities(string format)
    {
        using var fixture = await PathIdentityFixture.CreateAsync(format);
        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["report.txt"] = "ordinary root filename",
            [" report.txt"] = "leading ASCII space filename",
            ["\u00a0report.txt"] = "leading nonbreaking space filename",
            ["report.txt\u00a0"] = "trailing nonbreaking space filename",
            ["\u00a0"] = "whitespace-only native filename",
            ["documents/report.txt"] = "ordinary folder and filename",
            [" documents/report.txt"] = "leading ASCII space folder",
            ["documents/ report.txt"] = "ordinary folder and leading space filename",
            [" documents/ report.txt"] = "both components start with ASCII space",
            ["\u00a0documents\u00a0/report.txt"] = "nonbreaking spaces around a folder name",
        };
        if (!OperatingSystem.IsWindows())
        {
            expectedFiles["folder/C:file"] = "valid nested Unix colon filename";
        }
        string[] expectedEmptyDirectories = ["empty", " empty", "\u00a0empty\u00a0"];
        await WriteFilesAsync(fixture.SourceRoot, expectedFiles);
        foreach (var directory in expectedEmptyDirectories)
        {
            Directory.CreateDirectory(GetNativePath(fixture.SourceRoot, directory));
        }
        AssertNativeFiles(fixture.SourceRoot, expectedFiles);

        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        var repeatedBackup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        AssertNoDifferences(repeatedBackup);
        var verification = await fixture.Synchronizer.VerifyAsync(new(fixture.SourceRoot, ByteForByte: true));
        AssertNoDifferences(verification);

        if (format == MirrorArchiveFormatName.Value)
        {
            AssertNativeFiles(fixture.ArchiveRoot, expectedFiles);
            foreach (var directory in expectedEmptyDirectories)
            {
                Assert.IsTrue
                (
                    File.Exists(GetNativePath(fixture.ArchiveRoot, $"{directory}/.yabt-empty")),
                    $"The exact empty-directory path '{directory}' must be represented in the archive."
                );
            }
        }
        else
        {
            AssertZipContents(fixture.ArchiveRoot, expectedFiles, expectedEmptyDirectories);
        }

        var restore = await fixture.Synchronizer.RestoreAsync
        (
            new(fixture.SourceRoot, ByteForByte: true, DestinationRoot: fixture.RestoreRoot)
        );

        Assert.IsTrue(restore.Completed, restore.Message);
        AssertNativeFiles(fixture.RestoreRoot, expectedFiles);
        AssertNativeDirectories(fixture.RestoreRoot, expectedFiles.Keys, expectedEmptyDirectories);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.RestoreRoot, ".yabt-empty", SearchOption.AllDirectories).Any());
        var repeatedRestore = await fixture.Synchronizer.RestoreAsync
        (
            new(fixture.SourceRoot, DestinationRoot: fixture.RestoreRoot)
        );
        AssertNoDifferences(repeatedRestore);
        CollectionAssert.AreEqual
        (
            File.ReadAllBytes(Path.Combine(fixture.SourceRoot, FolderPolicyFileNames.Primary)),
            File.ReadAllBytes(Path.Combine(fixture.RestoreRoot, FolderPolicyFileNames.Primary))
        );
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value)]
    [DataRow(ZipArchiveFormatName.Value)]
    public async Task BackupOfSelectedLeadingSpaceDirectoryDoesNotReadItsUnspacedSibling(string format)
    {
        using var fixture = await PathIdentityFixture.CreateAsync(MirrorArchiveFormatName.Value);
        var selectedRoot = Path.Combine(fixture.SourceRoot, " documents");
        var unselectedRoot = Path.Combine(fixture.SourceRoot, "documents");
        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["important.txt"] = "selected spaced folder payload",
            [" important.txt"] = "selected spaced filename payload",
        };
        await WriteFilesAsync(selectedRoot, expectedFiles);
        await WriteTextAsync(Path.Combine(unselectedRoot, "important.txt"), "unselected sibling decoy");
        await WriteTextAsync(Path.Combine(unselectedRoot, "unselected.txt"), "must never enter the archive");
        await WritePolicyAsync(selectedRoot, format);

        var backup = await fixture.Synchronizer.BackupAsync(new(selectedRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        var verification = await fixture.Synchronizer.VerifyAsync(new(selectedRoot, ByteForByte: true));
        AssertNoDifferences(verification);
        if (format == MirrorArchiveFormatName.Value)
        {
            AssertNativeFiles(fixture.ArchiveRoot, expectedFiles);
        }
        else
        {
            AssertZipContents(fixture.ArchiveRoot, expectedFiles, []);
        }

        var restore = await fixture.Synchronizer.RestoreAsync
        (
            new(fixture.SourceRoot, ByteForByte: true, DestinationRoot: fixture.RestoreRoot)
        );

        Assert.IsTrue(restore.Completed, restore.Message);
        AssertNativeFiles(fixture.RestoreRoot, expectedFiles);
        Assert.AreEqual("unselected sibling decoy", File.ReadAllText(Path.Combine(unselectedRoot, "important.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.RestoreRoot, "unselected.txt")));
    }

    [TestMethod]
    [DataRow("report.txt ")]
    [DataRow("documents /important.txt")]
    public async Task RestoreRejectsUnrepresentableArchiveWhitespaceBeforeDestinationMutation(string archivePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Trailing ASCII spaces are valid native names on other supported filesystems.");
        }

        var archive = new MemoryObjectStore();
        using var fixture = await PathIdentityFixture.CreateAsync(MirrorArchiveFormatName.Value, archive);
        var existingFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["report.txt"] = "existing unspaced filename",
            ["documents/important.txt"] = "existing unspaced directory",
            ["unrelated.txt"] = "must not be historized by rejected restore",
        };
        await WriteFilesAsync(fixture.RestoreRoot, existingFiles);
        var originalFiles = SnapshotFiles(fixture.RestoreRoot);
        var originalDirectories = GetRelativeDirectories(fixture.RestoreRoot);
        var archiveBytes = Encoding.UTF8.GetBytes("archive object whose exact path cannot be represented");
        await UploadBytesAsync(archive, archivePath, archiveBytes);
        var serializer = fixture.Services.GetRequiredService<IChangeManifestSerializer>();
        using var manifest = serializer.Create
        (
            [new
            (
                archivePath,
                ArchiveChangeFingerprint.Create(archiveBytes.LongLength, DateTimeOffset.UtcNow),
                ContentHash: ArchiveHash.Compute(archiveBytes)
            )],
            MirrorArchiveFormatName.Value
        );
        using var serializedManifest = new MemoryStream();
        await serializer.WriteAsync(manifest, serializedManifest);
        await UploadBytesAsync(archive, ArchiveChangeManifest.UncompressedFileName, serializedManifest.ToArray());

        var exception = await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => fixture.Synchronizer.RestoreAsync
            (
                new(fixture.SourceRoot, DestinationRoot: fixture.RestoreRoot)
            )
        );

        StringAssert.Contains(exception.ToString(), archivePath.Split('/')[0]);
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.RestoreRoot));
        CollectionAssert.AreEqual(originalDirectories, GetRelativeDirectories(fixture.RestoreRoot));
    }

    [TestMethod]
    public async Task BackupRejectsSelectedUnixDirectoryWithLiteralBackslashBeforeSelectingAnotherBranch()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A literal backslash is a native filename character only on Unix filesystems.");
        }

        using var fixture = await PathIdentityFixture.CreateAsync(MirrorArchiveFormatName.Value);
        var selectedRoot = Path.Combine(fixture.SourceRoot, "folder\\name");
        var unrelatedRoot = Path.Combine(fixture.SourceRoot, "folder", "name");
        await WriteTextAsync(Path.Combine(selectedRoot, "important.txt"), "selected literal-backslash payload");
        await WriteTextAsync(Path.Combine(unrelatedRoot, "important.txt"), "different nested branch payload");
        var originalFiles = SnapshotFiles(fixture.SourceRoot);

        var exception = await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => fixture.Synchronizer.BackupAsync(new(selectedRoot))
        );

        StringAssert.Contains(exception.Message, "literal backslash");
        Assert.IsFalse(Directory.Exists(fixture.ArchiveRoot));
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.SourceRoot));
    }

    [TestMethod]
    [DataRow("unsupported.", false)]
    [DataRow("unsupported.", true)]
    [DataRow("unsupported ", false)]
    [DataRow("unsupported ", true)]
    public async Task BackupRejectsSelectedWindowsNativeAliasBeforeSelectingItsSibling
    (
        string nativeName,
        bool useExtendedSelector
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Trailing-dot and trailing-space aliases are specific to Windows path handling.");
        }

        using var fixture = await PathIdentityFixture.CreateAsync(MirrorArchiveFormatName.Value);
        var fullEntryPath = Path.Combine(Path.GetFullPath(fixture.SourceRoot), nativeName);
        var extendedEntryPath = GetExtendedWindowsPath(fullEntryPath);
        var extendedFilePath = Path.Combine(extendedEntryPath, "important.txt");
        var siblingFilePath = Path.Combine(fixture.SourceRoot, "unsupported", "important.txt");
        await WriteTextAsync(siblingFilePath, "unselected sibling payload");
        Directory.CreateDirectory(extendedEntryPath);
        try
        {
            await File.WriteAllTextAsync(extendedFilePath, "selected native payload");
            Assert.IsTrue
            (
                Directory.EnumerateDirectories(fixture.SourceRoot)
                    .Select(Path.GetFileName)
                    .Contains(nativeName, StringComparer.Ordinal)
            );
            var selectedRoot = useExtendedSelector ? extendedEntryPath : fullEntryPath;

            var exception = await Assert.ThrowsAsync<YabtFileSystemException>
            (
                () => fixture.Synchronizer.BackupAsync(new(selectedRoot))
            );

            StringAssert.Contains(exception.Message, nativeName);
            Assert.IsFalse(Directory.Exists(fixture.ArchiveRoot));
            Assert.AreEqual("selected native payload", await File.ReadAllTextAsync(extendedFilePath));
            Assert.AreEqual("unselected sibling payload", await File.ReadAllTextAsync(siblingFilePath));
        }
        finally
        {
            Assert.IsTrue(fullEntryPath.StartsWith(fixture.SourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            File.Delete(extendedFilePath);
            Directory.Delete(extendedEntryPath, recursive: false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestoreRejectsExistingUnixLiteralBackslashEntryBeforeMutation(bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A literal backslash is a native filename character only on Unix filesystems.");
        }

        using var fixture = await PathIdentityFixture.CreateAsync(MirrorArchiveFormatName.Value);
        await WriteTextAsync(Path.Combine(fixture.SourceRoot, "report.txt"), "desired content");
        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        await WriteTextAsync(Path.Combine(fixture.RestoreRoot, "report.txt"), "existing destination content");
        var nativeEntry = Path.Combine(fixture.RestoreRoot, "folder\\name");
        if (isDirectory)
        {
            Directory.CreateDirectory(nativeEntry);
        }
        else
        {
            await WriteTextAsync(nativeEntry, "must remain at its exact native path");
        }
        var originalFiles = SnapshotFiles(fixture.RestoreRoot);
        var originalDirectories = GetRelativeDirectories(fixture.RestoreRoot);

        var exception = await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => fixture.Synchronizer.RestoreAsync
            (
                new(fixture.SourceRoot, DestinationRoot: fixture.RestoreRoot)
            )
        );

        StringAssert.Contains(exception.Message, "literal backslash");
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.RestoreRoot));
        CollectionAssert.AreEqual(originalDirectories, GetRelativeDirectories(fixture.RestoreRoot));
    }

    [TestMethod]
    [DataRow("unsupported.", false)]
    [DataRow("unsupported ", true)]
    public async Task RestoreRejectsExistingWindowsNativeAliasBeforeMutation(string nativeName, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Trailing-dot and trailing-space aliases are specific to Windows path handling.");
        }

        using var fixture = await PathIdentityFixture.CreateAsync(MirrorArchiveFormatName.Value);
        await WriteTextAsync(Path.Combine(fixture.SourceRoot, "report.txt"), "desired content");
        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        await WriteTextAsync(Path.Combine(fixture.RestoreRoot, "report.txt"), "existing destination content");
        var originalFiles = SnapshotFiles(fixture.RestoreRoot);
        var originalDirectories = GetRelativeDirectories(fixture.RestoreRoot);
        var fullEntryPath = Path.Combine(Path.GetFullPath(fixture.RestoreRoot), nativeName);
        var extendedEntryPath = GetExtendedWindowsPath(fullEntryPath);
        var created = false;
        try
        {
            if (isDirectory)
            {
                Directory.CreateDirectory(extendedEntryPath);
            }
            else
            {
                await File.WriteAllTextAsync(extendedEntryPath, "native identity sentinel");
            }
            created = true;
            Assert.IsTrue
            (
                Directory.EnumerateFileSystemEntries(fixture.RestoreRoot)
                    .Select(Path.GetFileName)
                    .Contains(nativeName, StringComparer.Ordinal),
                "The fixture must create the exact native spelling, not its Win32 alias."
            );

            var exception = await Assert.ThrowsAsync<Exception>
            (
                () => fixture.Synchronizer.RestoreAsync
                (
                    new(fixture.SourceRoot, DestinationRoot: fixture.RestoreRoot)
                )
            );

            Assert.IsInstanceOfType<YabtFileSystemException>(exception, exception.ToString());
            StringAssert.Contains(exception.Message, nativeName);
            if (isDirectory)
            {
                Assert.IsTrue(Directory.Exists(extendedEntryPath));
            }
            else
            {
                Assert.AreEqual("native identity sentinel", await File.ReadAllTextAsync(extendedEntryPath));
            }
        }
        finally
        {
            if (created)
            {
                Assert.IsTrue
                (
                    fullEntryPath.StartsWith(fixture.RestoreRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                );
                if (isDirectory)
                {
                    Directory.Delete(extendedEntryPath, recursive: false);
                }
                else
                {
                    File.Delete(extendedEntryPath);
                }
            }
        }

        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.RestoreRoot));
        CollectionAssert.AreEqual(originalDirectories, GetRelativeDirectories(fixture.RestoreRoot));
    }

    private static void AssertNoDifferences(SyncRunResult result)
    {
        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual(0, result.NewCount, result.Message);
        Assert.AreEqual(0, result.ChangedCount, result.Message);
        Assert.AreEqual(0, result.ExtraCount, result.Message);
    }

    private static void AssertNativeFiles(string rootPath, IReadOnlyDictionary<string, string> expectedFiles)
    {
        var actualPaths = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Select(path => GetRelativePath(rootPath, path))
            .Where
            (
                path => !IsRootMetadataOrPlumbing(path) &&
                    path != ArchiveFolderMarkerFileNames.EmptyFolder &&
                    !path.EndsWith($"/{ArchiveFolderMarkerFileNames.EmptyFolder}", StringComparison.Ordinal)
            )
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expectedFiles.Keys.Order(StringComparer.Ordinal).ToArray(), actualPaths);
        foreach (var pair in expectedFiles)
        {
            Assert.AreEqual(pair.Value, File.ReadAllText(GetNativePath(rootPath, pair.Key)), pair.Key);
        }
    }

    private static void AssertNativeDirectories
    (
        string rootPath,
        IEnumerable<string> filePaths,
        IEnumerable<string> emptyDirectories
    )
    {
        var expectedDirectories = new HashSet<string>(emptyDirectories, StringComparer.Ordinal);
        foreach (var filePath in filePaths)
        {
            var separator = filePath.LastIndexOf('/');
            while (separator >= 0)
            {
                var parentPath = filePath[..separator];
                expectedDirectories.Add(parentPath);
                separator = parentPath.LastIndexOf('/');
            }
        }
        var actualDirectories = GetRelativeDirectories(rootPath)
            .Where(path => !IsRootMetadataOrPlumbing(path))
            .ToArray();
        CollectionAssert.AreEqual(expectedDirectories.Order(StringComparer.Ordinal).ToArray(), actualDirectories);
    }

    private static void AssertZipContents
    (
        string archiveRoot,
        IReadOnlyDictionary<string, string> expectedFiles,
        IEnumerable<string> emptyDirectories
    )
    {
        var packages = Directory.GetFiles(archiveRoot, "*.zip", SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, packages);
        using var package = ZipFile.OpenRead(packages[0]);
        var expectedPaths = expectedFiles.Keys
            .Concat(emptyDirectories.Select(directory => $"{directory}/{ArchiveFolderMarkerFileNames.EmptyFolder}"))
            .Append(FolderPolicyFileNames.Primary)
            .Append(ArchivePackageManifestFileNames.EmbeddedEntryName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualPaths = package.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expectedPaths, actualPaths);
        foreach (var pair in expectedFiles)
        {
            var entry = package.GetEntry(pair.Key);
            Assert.IsNotNull(entry, pair.Key);
            using var content = entry.Open();
            using var reader = new StreamReader(content, Encoding.UTF8);
            Assert.AreEqual(pair.Value, reader.ReadToEnd(), pair.Key);
        }
    }

    private static bool IsRootMetadataOrPlumbing(string relativePath) =>
        relativePath is BackupRootFileNames.Primary or FolderPolicyFileNames.Primary or
            ArchiveChangeManifest.UncompressedFileName or ArchiveChangeManifest.BrotliFileName or
            ArchiveChangeManifest.InvalidationMarkerFileName or ArchiveLogicalStateManifest.FileName or
            ArchiveLogicalStateManifest.InvalidationMarkerFileName or ArchiveInternalFolderNames.TemporaryUploads or
            ".yabt-hist" ||
        relativePath.StartsWith($"{ArchiveInternalFolderNames.TemporaryUploads}/", StringComparison.Ordinal) ||
        relativePath.StartsWith(".yabt-hist/", StringComparison.Ordinal);

    private static string GetNativePath(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string GetExtendedWindowsPath(string fullPath) =>
        fullPath.StartsWith("\\\\", StringComparison.Ordinal) ?
            "\\\\?\\UNC\\" + fullPath[2..] :
            "\\\\?\\" + fullPath;

    private static string GetRelativePath(string rootPath, string fullPath) =>
        Path.GetRelativePath(rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static string[] GetRelativeDirectories(string rootPath) => Directory
        .EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
        .Select(path => GetRelativePath(rootPath, path))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] SnapshotFiles(string rootPath) => Directory
        .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select
        (
            path => string.Join
            (
                "|",
                GetRelativePath(rootPath, path),
                File.GetLastWriteTimeUtc(path).Ticks,
                Convert.ToBase64String(File.ReadAllBytes(path))
            )
        )
        .ToArray();

    private static async Task WriteFilesAsync(string rootPath, IReadOnlyDictionary<string, string> files)
    {
        foreach (var pair in files)
        {
            await WriteTextAsync(GetNativePath(rootPath, pair.Key), pair.Value);
        }
    }

    private static async Task WriteTextAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Task WritePolicyAsync(string rootPath, string format) => WriteTextAsync
    (
        Path.Combine(rootPath, FolderPolicyFileNames.Primary),
        JsonSerializer.Serialize(new { format })
    );

    private static async Task UploadBytesAsync(MemoryObjectStore store, string key, byte[] bytes)
    {
        using var content = new MemoryStream(bytes, writable: false);
        await store.UploadAsync(key, content, "application/octet-stream", new Dictionary<string, string>());
    }

    private sealed class PathIdentityMemoryStoreResolver(MemoryObjectStore _store) : IBackupRootStoreResolver
    {
        public const string StoreKindValue = "path-identity-memory";

        public string StoreKind => StoreKindValue;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _store;
    }

    private sealed class PathIdentityFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-path-identity-";

        private PathIdentityFixture(MemoryObjectStore? memoryArchive)
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(RootPath, "source");
            ArchiveRoot = Path.Combine(RootPath, "archive");
            RestoreRoot = Path.Combine(RootPath, "restored");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtMetadata();
            services.AddYabtSync();
            if (memoryArchive is not null)
            {
                services.AddSingleton<IBackupRootStoreResolver>(new PathIdentityMemoryStoreResolver(memoryArchive));
            }
            Services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string ArchiveRoot { get; }

        public string RestoreRoot { get; }

        public ServiceProvider Services { get; }

        public IArchiveSynchronizer Synchronizer => Services.GetRequiredService<IArchiveSynchronizer>();

        public static async Task<PathIdentityFixture> CreateAsync(string format, MemoryObjectStore? memoryArchive = null)
        {
            var fixture = new PathIdentityFixture(memoryArchive);
            try
            {
                Directory.CreateDirectory(fixture.SourceRoot);
                var store = memoryArchive is null ?
                    new BackupRootStore("archive", FileSystemObjectStoreKind.Value)
                    {
                        ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["rootPath"] = JsonSerializer.SerializeToElement(fixture.ArchiveRoot),
                        },
                    } :
                    new BackupRootStore("archive", PathIdentityMemoryStoreResolver.StoreKindValue);
                var descriptor = new BackupRootDescriptor
                (
                    BackupRootDescriptor.ExpectedDocumentType,
                    BackupRootDescriptor.ExpectedSchemaVersion,
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    ArchiveLayout.Default,
                    [store]
                );
                await using
                (
                    var content = File.Create(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary))
                )
                {
                    await fixture.Services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, content);
                }
                await WritePolicyAsync(fixture.SourceRoot, format);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Services.Dispose();
            var fullFixturePath = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            Assert.AreEqual(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(fullFixturePath));
            Assert.IsTrue(Path.GetFileName(fullFixturePath).StartsWith(FixturePrefix, StringComparison.Ordinal));
            if (Directory.Exists(fullFixturePath))
            {
                Directory.Delete(fullFixturePath, recursive: true);
            }
        }
    }
}
