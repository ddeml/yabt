using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Common;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Format.Mirror;
using Yabt.Format.Zip;
using Yabt.Metadata;
using Yabt.Sync.Implementation;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class DirectoryLinkSafetyTests
{
    [TestMethod]
    public async Task RestoreStagingRejectsLinkedTemporaryDirectoryBeforeCreatingExternalFolders()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        Directory.CreateDirectory(fixture.RestoreRoot);
        await WriteTextAsync(Path.Combine(fixture.OutsideRoot, "important.txt"), "outside sentinel");
        await fixture.CreateDirectoryLinkAsync
        (
            Path.Combine(fixture.RestoreRoot, ArchiveInternalFolderNames.TemporaryUploads),
            fixture.OutsideRoot
        );
        var originalFiles = SnapshotFiles(fixture.OutsideRoot);
        var restoreTarget = new FileSystemRestoreTarget(fixture.RestoreRoot, NullLogger.Instance);

        await Assert.ThrowsAsync<YabtSyncException>(() => restoreTarget.CreateStagingDirectoryAsync(default));

        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.OutsideRoot));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.OutsideRoot).Length);
    }

    [TestMethod]
    public async Task DeduplicateAsyncRejectsLinkedHistoryAncestorWithoutChangingExternalFiles()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync("linked/history");
        var historyPath = Path.Combine(fixture.OutsideRoot, "history");
        var content = Enumerable.Range(0, 20_000).Select(index => (byte)(index % 251)).ToArray();
        await WriteBytesAsync(Path.Combine(historyPath, "20260919T100000Z", "first.bin"), content);
        await WriteBytesAsync(Path.Combine(historyPath, "20260920T100000Z", "second.bin"), content);
        await WriteTextAsync(Path.Combine(fixture.OutsideRoot, "unrelated.txt"), "outside sentinel");
        await fixture.CreateDirectoryLinkAsync
        (
            Path.Combine(fixture.ArchiveRoot, "linked"),
            fixture.OutsideRoot
        );
        var originalFiles = SnapshotFiles(fixture.OutsideRoot);

        await Assert.ThrowsAsync<YabtException>
        (
            () => fixture.Services.GetRequiredService<IHistoryDeduplicator>().DeduplicateAsync
            (
                new HistoryDeduplicationRequest(fixture.SourceRoot)
            )
        );

        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.OutsideRoot));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsyncFollowsApprovedMirrorLinkAndRestoresNativeDirectories
    (
        bool useRelativeExpectedTarget
    )
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        await WriteTextAsync(Path.Combine(fixture.OutsideRoot, "nested", "important.txt"), "linked content");
        Directory.CreateDirectory(Path.Combine(fixture.OutsideRoot, "empty"));
        await fixture.CreateDirectoryLinkAsync
        (
            Path.Combine(fixture.SourceRoot, "linked"),
            fixture.OutsideRoot
        );
        var expectedTarget = useRelativeExpectedTarget ?
            Path.GetRelativePath(fixture.SourceRoot, fixture.OutsideRoot) :
            fixture.OutsideRoot;
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "linked", expectedTarget);
        var originalFiles = SnapshotFiles(fixture.OutsideRoot);

        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        Assert.AreEqual
        (
            "linked content",
            await File.ReadAllTextAsync(Path.Combine(fixture.ArchiveRoot, "linked", "nested", "important.txt"))
        );
        var restore = await fixture.Synchronizer.RestoreAsync
        (
            new(fixture.SourceRoot, DestinationRoot: fixture.RestoreRoot)
        );

        Assert.IsTrue(restore.Completed, restore.Message);
        Assert.AreEqual
        (
            "linked content",
            await File.ReadAllTextAsync(Path.Combine(fixture.RestoreRoot, "linked", "nested", "important.txt"))
        );
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.RestoreRoot, "linked", "empty")));
        Assert.AreEqual
        (
            (FileAttributes)0,
            File.GetAttributes(Path.Combine(fixture.RestoreRoot, "linked")) & FileAttributes.ReparsePoint
        );
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.OutsideRoot, "empty")));
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.OutsideRoot));
    }

    [TestMethod]
    public async Task BackupAsyncRejectsApprovedLinkWhenItsTargetDoesNotMatch()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        await WriteTextAsync(Path.Combine(fixture.OutsideRoot, "important.txt"), "must remain outside");
        var differentTarget = Path.Combine(fixture.RootPath, "different");
        Directory.CreateDirectory(differentTarget);
        await fixture.CreateDirectoryLinkAsync
        (
            Path.Combine(fixture.SourceRoot, "linked"),
            fixture.OutsideRoot
        );
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "linked", differentTarget);
        await fixture.AddUnrelatedArchiveFileAsync();
        var originalFiles = SnapshotFiles(fixture.OutsideRoot);

        await AssertBackupRejectedAsync(fixture);

        fixture.AssertNoSuccessfulManifestOrUnrelatedRemoval();
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ArchiveRoot, "linked", "important.txt")));
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.OutsideRoot));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BackupAsyncValidatesApprovalWhenLinkedDirectoryIsSelectedDirectly
    (
        bool expectedTargetMatches
    )
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var selectedDirectory = Path.Combine(fixture.SourceRoot, "linked");
        var otherDirectory = Path.Combine(fixture.RootPath, "different");
        Directory.CreateDirectory(otherDirectory);
        await WriteTextAsync(Path.Combine(fixture.OutsideRoot, "important.txt"), "directly selected content");
        await fixture.CreateDirectoryLinkAsync(selectedDirectory, fixture.OutsideRoot);
        await WritePolicyAsync
        (
            fixture.SourceRoot,
            MirrorArchiveFormatName.Value,
            "linked",
            expectedTargetMatches ? fixture.OutsideRoot : otherDirectory
        );
        if (!expectedTargetMatches)
        {
            await fixture.AddUnrelatedArchiveFileAsync();

            await AssertBackupRejectedAsync(fixture, sourceRoot: selectedDirectory);

            fixture.AssertNoSuccessfulManifestOrUnrelatedRemoval();
            Assert.IsFalse(File.Exists(Path.Combine(fixture.ArchiveRoot, "important.txt")));
            return;
        }

        var result = await fixture.Synchronizer.BackupAsync(new(selectedDirectory));

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual
        (
            "directly selected content",
            await File.ReadAllTextAsync(Path.Combine(fixture.ArchiveRoot, "important.txt"))
        );
    }

    [TestMethod]
    public async Task BackupAsyncResolvesNestedExpectedTargetRelativeToExternalPhysicalParent()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var externalParent = Path.Combine(fixture.OutsideRoot, "parent");
        var payloadDirectory = Path.Combine(fixture.OutsideRoot, "payload");
        Directory.CreateDirectory(externalParent);
        await WriteTextAsync(Path.Combine(payloadDirectory, "important.txt"), "nested relative content");
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, "linked"), externalParent);
        await fixture.CreateDirectoryLinkAsync(Path.Combine(externalParent, "child"), payloadDirectory);
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "linked", externalParent);
        await WritePolicyAsync(externalParent, MirrorArchiveFormatName.Value, "child", "../payload");
        var originalFiles = SnapshotFiles(payloadDirectory);

        var result = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual
        (
            "nested relative content",
            await File.ReadAllTextAsync(Path.Combine(fixture.ArchiveRoot, "linked", "child", "important.txt"))
        );
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(payloadDirectory));
    }

    [TestMethod]
    public async Task RestoreAsyncDoesNotTreatSourceFollowApprovalAsPermissionToMutateThroughLink()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var linkedDirectory = Path.Combine(fixture.SourceRoot, "linked");
        var nativeFile = Path.Combine(fixture.SourceRoot, "native.txt");
        var externalFile = Path.Combine(fixture.OutsideRoot, "important.txt");
        await WriteTextAsync(nativeFile, "archived native content");
        await WriteTextAsync(externalFile, "archived linked content");
        await fixture.CreateDirectoryLinkAsync(linkedDirectory, fixture.OutsideRoot);
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "linked", fixture.OutsideRoot);
        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        await WriteTextAsync(nativeFile, "changed native content");
        await WriteTextAsync(externalFile, "changed external content");
        var originalFiles = SnapshotFiles(fixture.OutsideRoot);

        await Assert.ThrowsAsync<YabtSyncException>
        (
            () => fixture.Synchronizer.RestoreAsync
            (
                new(fixture.SourceRoot, DestinationRoot: fixture.SourceRoot)
            )
        );

        Assert.AreEqual("changed native content", await File.ReadAllTextAsync(nativeFile));
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.OutsideRoot));
        Assert.AreNotEqual((FileAttributes)0, File.GetAttributes(linkedDirectory) & FileAttributes.ReparsePoint);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.SourceRoot, ".yabt-hist")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.SourceRoot, ArchiveLogicalStateManifest.FileName)));
    }

    [TestMethod]
    public async Task BackupAsyncRejectsApprovedSelfCycleWithoutRemovingUnrelatedArchiveContent()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        await fixture.CreateDirectoryLinkAsync
        (
            Path.Combine(fixture.SourceRoot, "loop"),
            fixture.SourceRoot
        );
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "loop", ".");
        await fixture.AddUnrelatedArchiveFileAsync();

        await AssertBackupRejectedAsync(fixture, requireCycleDiagnostic: true);

        fixture.AssertNoSuccessfulManifestOrUnrelatedRemoval();
    }

    [TestMethod]
    public async Task BackupAsyncRejectsApprovedTwoDirectoryCycle()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var first = Path.Combine(fixture.OutsideRoot, "first");
        var second = Path.Combine(fixture.OutsideRoot, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, "linked"), first);
        await fixture.CreateDirectoryLinkAsync(Path.Combine(first, "next"), second);
        await fixture.CreateDirectoryLinkAsync(Path.Combine(second, "next"), first);
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "linked", first);
        await WritePolicyAsync(first, MirrorArchiveFormatName.Value, "next", "../second");
        await WritePolicyAsync(second, MirrorArchiveFormatName.Value, "next", "../first");
        await fixture.AddUnrelatedArchiveFileAsync();

        await AssertBackupRejectedAsync(fixture, requireCycleDiagnostic: true);

        fixture.AssertNoSuccessfulManifestOrUnrelatedRemoval();
    }

    [TestMethod]
    public async Task BackupAsyncRejectsCycleAcrossNestedZipProjection()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var packagedDirectory = Path.Combine(fixture.SourceRoot, "packaged");
        Directory.CreateDirectory(packagedDirectory);
        await WriteTextAsync(Path.Combine(packagedDirectory, "important.txt"), "packaged content");
        await fixture.CreateDirectoryLinkAsync(Path.Combine(packagedDirectory, "again"), fixture.SourceRoot);
        await WritePolicyAsync(packagedDirectory, ZipArchiveFormatName.Value, "again", "..");
        await fixture.AddUnrelatedArchiveFileAsync();

        await AssertBackupRejectedAsync(fixture, requireCycleDiagnostic: true);

        fixture.AssertNoSuccessfulManifestOrUnrelatedRemoval();
    }

    [TestMethod]
    public async Task BackupAsyncFollowsApprovedLinkInsideNestedZipAndRestoresPolicyUnchanged()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var packagedDirectory = Path.Combine(fixture.SourceRoot, "packaged");
        Directory.CreateDirectory(packagedDirectory);
        await WriteTextAsync(Path.Combine(fixture.OutsideRoot, "nested", "important.txt"), "packaged linked content");
        Directory.CreateDirectory(Path.Combine(fixture.OutsideRoot, "empty"));
        await fixture.CreateDirectoryLinkAsync(Path.Combine(packagedDirectory, "linked"), fixture.OutsideRoot);
        await WritePolicyAsync(packagedDirectory, ZipArchiveFormatName.Value, "linked", fixture.OutsideRoot);
        var policyBytes = await File.ReadAllBytesAsync(Path.Combine(packagedDirectory, FolderPolicyFileNames.Primary));
        var originalFiles = SnapshotFiles(fixture.OutsideRoot);

        var backup = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        var restore = await fixture.Synchronizer.RestoreAsync
        (
            new(fixture.SourceRoot, DestinationRoot: fixture.RestoreRoot)
        );

        Assert.IsTrue(restore.Completed, restore.Message);
        var restoredPackageDirectory = Path.Combine(fixture.RestoreRoot, "packaged");
        Assert.AreEqual
        (
            "packaged linked content",
            await File.ReadAllTextAsync(Path.Combine(restoredPackageDirectory, "linked", "nested", "important.txt"))
        );
        Assert.IsTrue(Directory.Exists(Path.Combine(restoredPackageDirectory, "linked", "empty")));
        Assert.AreEqual
        (
            (FileAttributes)0,
            File.GetAttributes(Path.Combine(restoredPackageDirectory, "linked")) & FileAttributes.ReparsePoint
        );
        CollectionAssert.AreEqual
        (
            policyBytes,
            await File.ReadAllBytesAsync(Path.Combine(restoredPackageDirectory, FolderPolicyFileNames.Primary))
        );
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.OutsideRoot, "empty")));
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(fixture.OutsideRoot));
    }

    [TestMethod]
    public async Task BackupAsyncPreservesDistinctApprovedAliasesOfTheSamePhysicalDirectory()
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        await WriteTextAsync(Path.Combine(fixture.OutsideRoot, "important.txt"), "shared physical content");
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, "first"), fixture.OutsideRoot);
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, "second"), fixture.OutsideRoot);
        await WritePolicyAsync
        (
            fixture.SourceRoot,
            MirrorArchiveFormatName.Value,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["first"] = fixture.OutsideRoot,
                ["second"] = fixture.OutsideRoot,
            }
        );

        var result = await fixture.Synchronizer.BackupAsync(new(fixture.SourceRoot));

        Assert.IsTrue(result.Completed, result.Message);
        Assert.AreEqual
        (
            "shared physical content",
            await File.ReadAllTextAsync(Path.Combine(fixture.ArchiveRoot, "first", "important.txt"))
        );
        Assert.AreEqual
        (
            "shared physical content",
            await File.ReadAllTextAsync(Path.Combine(fixture.ArchiveRoot, "second", "important.txt"))
        );
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(".yabt-hist")]
    [DataRow(".yabt-tmp")]
    public async Task BackupAsyncRejectsApprovedLinkIntoSelectedArchive
    (
        string archiveRelativeTarget
    )
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var forbiddenTarget = Path.Combine(fixture.ArchiveRoot, archiveRelativeTarget);
        Directory.CreateDirectory(forbiddenTarget);
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, "linked"), forbiddenTarget);
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "linked", forbiddenTarget);
        await fixture.AddUnrelatedArchiveFileAsync();

        await AssertBackupRejectedAsync(fixture);

        fixture.AssertNoSuccessfulManifestOrUnrelatedRemoval();
    }

    [TestMethod]
    [DataRow(".yabt-hist")]
    [DataRow(".yabt-tmp")]
    public async Task BackupAsyncRejectsApprovedLinkIntoSourceHistoryOrPlumbing
    (
        string sourceRelativeTarget
    )
    {
        using var fixture = await DirectoryLinkFixture.CreateAsync();
        var forbiddenTarget = Path.Combine(fixture.SourceRoot, sourceRelativeTarget);
        await WriteTextAsync(Path.Combine(forbiddenTarget, "important.txt"), "source internal sentinel");
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, "linked"), forbiddenTarget);
        await WritePolicyAsync(fixture.SourceRoot, MirrorArchiveFormatName.Value, "linked", forbiddenTarget);
        await fixture.AddUnrelatedArchiveFileAsync();
        var originalFiles = SnapshotFiles(forbiddenTarget);

        await AssertBackupRejectedAsync(fixture);

        fixture.AssertNoSuccessfulManifestOrUnrelatedRemoval();
        CollectionAssert.AreEqual(originalFiles, SnapshotFiles(forbiddenTarget));
    }

    private static async Task AssertBackupRejectedAsync
    (
        DirectoryLinkFixture fixture,
        bool requireCycleDiagnostic = false,
        string? sourceRoot = null
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string failureDetails;
        try
        {
            var result = await fixture.Synchronizer.BackupAsync(new(sourceRoot ?? fixture.SourceRoot), timeout.Token);
            Assert.IsFalse(result.Completed, "An unsafe source traversal must not report a successful backup.");
            failureDetails = result.Message;
        }
        catch (YabtException exception)
        {
            // A traversal may fail during preflight or be reported as an incomplete backup.
            failureDetails = exception.ToString();
        }

        Assert.IsFalse(timeout.IsCancellationRequested, "Link traversal must reject a cycle without timing out.");
        if (requireCycleDiagnostic)
        {
            Assert.IsTrue
            (
                failureDetails.Contains("cycle", StringComparison.OrdinalIgnoreCase) ||
                    failureDetails.Contains("ancestor", StringComparison.OrdinalIgnoreCase),
                $"Traversal must diagnose the actual cycle: {failureDetails}"
            );
        }
    }

    private static Task WritePolicyAsync
    (
        string parentPath,
        string format,
        string childName,
        string expectedTarget
    ) => WritePolicyAsync
    (
        parentPath,
        format,
        new Dictionary<string, string>(StringComparer.Ordinal) { [childName] = expectedTarget }
    );

    private static Task WritePolicyAsync
    (
        string parentPath,
        string format,
        IReadOnlyDictionary<string, string> links
    )
    {
        var policy = new
        {
            format,
            directoryLinks = links.ToDictionary
            (
                pair => pair.Key,
                pair => new { behavior = "follow", expectedTarget = pair.Value }
            ),
        };
        return WriteTextAsync
        (
            Path.Combine(parentPath, FolderPolicyFileNames.Primary),
            JsonSerializer.Serialize(policy)
        );
    }

    private static Task WriteTextAsync(string path, string content) =>
        WriteBytesAsync(path, Encoding.UTF8.GetBytes(content));

    private static async Task WriteBytesAsync(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
    }

    private static string[] SnapshotFiles(string rootPath) => Directory
        .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select
        (
            path => string.Join
            (
                "|",
                Path.GetRelativePath(rootPath, path),
                File.GetLastWriteTimeUtc(path).Ticks,
                File.GetAttributes(path),
                Convert.ToBase64String(File.ReadAllBytes(path))
            )
        )
        .ToArray();

    private sealed class DirectoryLinkFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-directory-links-";
        private const string UnrelatedArchiveContent = "keep this existing archive object";
        private readonly List<string> _directoryLinks = [];

        private DirectoryLinkFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(RootPath, "source");
            ArchiveRoot = Path.Combine(RootPath, "archive");
            OutsideRoot = Path.Combine(RootPath, "outside");
            RestoreRoot = Path.Combine(RootPath, "restored");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtMetadata();
            services.AddYabtSync();
            Services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string ArchiveRoot { get; }

        public string OutsideRoot { get; }

        public string RestoreRoot { get; }

        public ServiceProvider Services { get; }

        public IArchiveSynchronizer Synchronizer => Services.GetRequiredService<IArchiveSynchronizer>();

        public static async Task<DirectoryLinkFixture> CreateAsync(string historyPrefix = ".yabt-hist")
        {
            var fixture = new DirectoryLinkFixture();
            try
            {
                Directory.CreateDirectory(fixture.SourceRoot);
                Directory.CreateDirectory(fixture.ArchiveRoot);
                Directory.CreateDirectory(fixture.OutsideRoot);
                var store = new BackupRootStore("archive", FileSystemObjectStoreKind.Value)
                {
                    ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["rootPath"] = JsonSerializer.SerializeToElement(fixture.ArchiveRoot),
                    },
                };
                var descriptor = new BackupRootDescriptor
                (
                    BackupRootDescriptor.ExpectedDocumentType,
                    BackupRootDescriptor.ExpectedSchemaVersion,
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    new ArchiveLayout(HistPrefix: historyPrefix),
                    [store]
                );
                await using var content = File.Create(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary));
                await fixture.Services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, content);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public async Task CreateDirectoryLinkAsync(string linkPath, string targetPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            if (OperatingSystem.IsWindows())
            {
                var script = $"New-Item -ItemType Junction -Path '{linkPath.Replace("'", "''")}' " +
                    $"-Value '{targetPath.Replace("'", "''")}' -ErrorAction Stop | Out-Null";
                var startInfo = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-EncodedCommand");
                startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                using var process = Process.Start(startInfo) ??
                    throw new InvalidOperationException("Could not start PowerShell to create a test junction.");
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.AreEqual(0, process.ExitCode, $"Creating the Windows junction failed: {await error} {await output}");
            }
            else
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }

            _directoryLinks.Add(linkPath);
            Assert.AreNotEqual((FileAttributes)0, File.GetAttributes(linkPath) & FileAttributes.ReparsePoint);
        }

        public Task AddUnrelatedArchiveFileAsync() => WriteTextAsync
        (
            Path.Combine(ArchiveRoot, "unrelated.txt"),
            UnrelatedArchiveContent
        );

        public void AssertNoSuccessfulManifestOrUnrelatedRemoval()
        {
            Assert.AreEqual(UnrelatedArchiveContent, File.ReadAllText(Path.Combine(ArchiveRoot, "unrelated.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(ArchiveRoot, ArchiveChangeManifest.UncompressedFileName)));
            Assert.IsFalse(File.Exists(Path.Combine(ArchiveRoot, ArchiveChangeManifest.BrotliFileName)));
        }

        public void Dispose()
        {
            Services.Dispose();
            var fullFixturePath = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            Assert.AreEqual(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(fullFixturePath));
            Assert.IsTrue(Path.GetFileName(fullFixturePath).StartsWith(FixturePrefix, StringComparison.Ordinal));
            foreach (var linkPath in Enumerable.Reverse(_directoryLinks))
            {
                Assert.IsTrue
                (
                    Path.GetFullPath(linkPath).StartsWith
                    (
                        fullFixturePath + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    )
                );
                Directory.Delete(linkPath, recursive: false);
            }
            if (Directory.Exists(fullFixturePath))
            {
                Directory.Delete(fullFixturePath, recursive: true);
            }
        }
    }
}
