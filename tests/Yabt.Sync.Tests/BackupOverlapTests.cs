using System.Diagnostics;
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
public sealed class BackupOverlapTests
{
    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value, "backup", "equal")]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run", "equal")]
    [DataRow(MirrorArchiveFormatName.Value, "verify", "equal")]
    [DataRow(ZipArchiveFormatName.Value, "backup", "equal")]
    [DataRow(ZipArchiveFormatName.Value, "dry-run", "equal")]
    [DataRow(ZipArchiveFormatName.Value, "verify", "equal")]
    [DataRow(MirrorArchiveFormatName.Value, "backup", "target-inside-source")]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run", "target-inside-source")]
    [DataRow(MirrorArchiveFormatName.Value, "verify", "target-inside-source")]
    [DataRow(ZipArchiveFormatName.Value, "backup", "target-inside-source")]
    [DataRow(ZipArchiveFormatName.Value, "dry-run", "target-inside-source")]
    [DataRow(ZipArchiveFormatName.Value, "verify", "target-inside-source")]
    [DataRow(MirrorArchiveFormatName.Value, "backup", "source-inside-target")]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run", "source-inside-target")]
    [DataRow(MirrorArchiveFormatName.Value, "verify", "source-inside-target")]
    [DataRow(ZipArchiveFormatName.Value, "backup", "source-inside-target")]
    [DataRow(ZipArchiveFormatName.Value, "dry-run", "source-inside-target")]
    [DataRow(ZipArchiveFormatName.Value, "verify", "source-inside-target")]
    public async Task OverlappingRootsAreRejectedBeforeAnyFilesystemMutation
    (
        string format,
        string operation,
        string arrangement
    )
    {
        using var fixture = new BackupOverlapFixture();
        var sourceRoot = Path.Combine(fixture.RootPath, "source");
        var archiveRoot = arrangement switch
        {
            "equal" => sourceRoot,
            "target-inside-source" => Path.Combine(sourceRoot, "archive"),
            "source-inside-target" => fixture.RootPath,
            _ => throw new ArgumentOutOfRangeException(nameof(arrangement)),
        };
        await fixture.ConfigureAsync(sourceRoot, archiveRoot, format);
        Directory.CreateDirectory(archiveRoot);
        await File.WriteAllTextAsync(Path.Combine(archiveRoot, "archive-sentinel.txt"), "preserve archive contents");

        await AssertRejectedWithoutMutationAsync(fixture, sourceRoot, operation);
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value, "backup")]
    [DataRow(MirrorArchiveFormatName.Value, "dry-run")]
    [DataRow(MirrorArchiveFormatName.Value, "verify")]
    [DataRow(ZipArchiveFormatName.Value, "backup")]
    [DataRow(ZipArchiveFormatName.Value, "dry-run")]
    [DataRow(ZipArchiveFormatName.Value, "verify")]
    public async Task MissingNestedTargetIsRejectedBeforeItIsInitialized(string format, string operation)
    {
        using var fixture = new BackupOverlapFixture();
        var sourceRoot = Path.Combine(fixture.RootPath, "source");
        var archiveRoot = Path.Combine(sourceRoot, "missing-parent", "archive");
        await fixture.ConfigureAsync(sourceRoot, archiveRoot, format);

        await AssertRejectedWithoutMutationAsync(fixture, sourceRoot, operation);

        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(archiveRoot)));
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value, "hist")]
    [DataRow(ZipArchiveFormatName.Value, "hist")]
    [DataRow(MirrorArchiveFormatName.Value, ".yabt-tmp")]
    [DataRow(ZipArchiveFormatName.Value, ".yabt-tmp")]
    public async Task ExplicitLivePrefixDoesNotHideOverlapWithTargetHistoryOrStaging
    (
        string format,
        string targetArea
    )
    {
        using var fixture = new BackupOverlapFixture();
        var archiveRoot = Path.Combine(fixture.RootPath, "archive");
        var sourceRoot = Path.Combine(archiveRoot, targetArea, "source");
        await fixture.ConfigureAsync(sourceRoot, archiveRoot, format, new("live", "hist"));

        // The source payload is outside archive/live, but still inside the archive's
        // history or staging namespace. Comparing only live prefixes would miss it.
        await AssertRejectedWithoutMutationAsync(fixture, sourceRoot, "backup");
    }

    [TestMethod]
    public async Task TargetMustNotContainSelectedPolicyOutsideExplicitLiveSource()
    {
        using var fixture = new BackupOverlapFixture();
        var descriptorRoot = Path.Combine(fixture.RootPath, "configured");
        var selectedRoot = Path.Combine(descriptorRoot, "selected");
        var payloadRoot = Path.Combine(descriptorRoot, "live", "selected");
        await fixture.ConfigureAsync(descriptorRoot, selectedRoot, MirrorArchiveFormatName.Value, new("live", "hist"));
        Directory.CreateDirectory(selectedRoot);
        Directory.CreateDirectory(payloadRoot);
        await WritePolicyAsync(selectedRoot, MirrorArchiveFormatName.Value);
        await File.WriteAllTextAsync(Path.Combine(payloadRoot, "selected.txt"), "selected source content");

        await AssertRejectedWithoutMutationAsync(fixture, selectedRoot, "backup");
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value)]
    [DataRow(ZipArchiveFormatName.Value)]
    public async Task PrefixSharingSiblingRootsRemainUsable(string format)
    {
        using var fixture = new BackupOverlapFixture();
        var sourceRoot = Path.Combine(fixture.RootPath, "source");
        var archiveRoot = Path.Combine(fixture.RootPath, "source-archive");
        await fixture.ConfigureAsync(sourceRoot, archiveRoot, format);

        await AssertBackupAndVerificationSucceedAsync(fixture, sourceRoot);
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value)]
    [DataRow(ZipArchiveFormatName.Value)]
    public async Task SelectedSourceSubtreeMayUseSiblingTargetUnderDescriptorRoot(string format)
    {
        using var fixture = new BackupOverlapFixture();
        var descriptorRoot = Path.Combine(fixture.RootPath, "configured");
        var selectedRoot = Path.Combine(descriptorRoot, "selected");
        var archiveRoot = Path.Combine(descriptorRoot, "archive");
        await fixture.ConfigureAsync(descriptorRoot, archiveRoot, format);
        Directory.CreateDirectory(selectedRoot);
        await WritePolicyAsync(selectedRoot, format);
        await File.WriteAllTextAsync(Path.Combine(selectedRoot, "selected.txt"), "selected source content");
        var sourceBefore = SnapshotTree(selectedRoot);

        await AssertBackupAndVerificationSucceedAsync(fixture, selectedRoot);

        CollectionAssert.AreEqual(sourceBefore, SnapshotTree(selectedRoot));
        Assert.IsFalse(Directory.Exists(Path.Combine(selectedRoot, ArchiveInternalFolderNames.TemporaryUploads)));
    }

    [TestMethod]
    [DataRow("source-root")]
    [DataRow("target-root")]
    [DataRow("source-ancestor")]
    [DataRow("target-ancestor")]
    [DataRow("target-ancestor-with-missing-tail")]
    public async Task DirectoryAliasesCannotHideOverlappingRoots(string arrangement)
    {
        using var fixture = new BackupOverlapFixture();
        var physicalRoot = Path.Combine(fixture.RootPath, "physical");
        var physicalSource = Path.Combine(physicalRoot, "source");
        var aliasRoot = Path.Combine(fixture.RootPath, "alias");
        Directory.CreateDirectory(physicalSource);
        var sourceRoot = physicalSource;
        var archiveRoot = physicalSource;
        switch (arrangement)
        {
            case "source-root":
                await fixture.CreateDirectoryLinkAsync(aliasRoot, physicalSource);
                sourceRoot = aliasRoot;
                break;
            case "target-root":
                await fixture.CreateDirectoryLinkAsync(aliasRoot, physicalSource);
                archiveRoot = aliasRoot;
                break;
            case "source-ancestor":
                await fixture.CreateDirectoryLinkAsync(aliasRoot, physicalRoot);
                sourceRoot = Path.Combine(aliasRoot, "source");
                break;
            case "target-ancestor":
                await fixture.CreateDirectoryLinkAsync(aliasRoot, physicalRoot);
                archiveRoot = Path.Combine(aliasRoot, "source");
                break;
            case "target-ancestor-with-missing-tail":
                await fixture.CreateDirectoryLinkAsync(aliasRoot, physicalRoot);
                archiveRoot = Path.Combine(aliasRoot, "source", "missing", "archive");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arrangement));
        }
        await fixture.ConfigureAsync(sourceRoot, archiveRoot, MirrorArchiveFormatName.Value);

        await AssertRejectedWithoutMutationAsync(fixture, sourceRoot, "backup");
    }

    [TestMethod]
    [DataRow(MirrorArchiveFormatName.Value)]
    [DataRow(ZipArchiveFormatName.Value)]
    public async Task UnknownCrossProviderRelationshipDoesNotBlockBackup(string format)
    {
        var archive = new MemoryObjectStore();
        using var fixture = new BackupOverlapFixture(archive);
        var sourceRoot = Path.Combine(fixture.RootPath, "source");
        await fixture.ConfigureAsync(sourceRoot, archiveRoot: null, format);
        var sourceBefore = SnapshotTree(sourceRoot);

        await AssertBackupAndVerificationSucceedAsync(fixture, sourceRoot);

        Assert.IsTrue(await archive.ExistsAsync(BackupRootFileNames.Primary));
        CollectionAssert.AreEqual(sourceBefore, SnapshotTree(sourceRoot));
    }

    private static async Task AssertRejectedWithoutMutationAsync
    (
        BackupOverlapFixture fixture,
        string sourceRoot,
        string operation
    )
    {
        var before = SnapshotTree(fixture.RootPath);
        var exception = await Assert.ThrowsAsync<YabtFileSystemException>
        (
            () => operation switch
            {
                "backup" => fixture.Synchronizer.BackupAsync(new(sourceRoot)),
                "dry-run" => fixture.Synchronizer.BackupAsync(new(sourceRoot, DryRun: true)),
                "verify" => fixture.Synchronizer.VerifyAsync(new(sourceRoot)),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            }
        );

        StringAssert.Contains(exception.Message.ToLowerInvariant(), "overlap");
        CollectionAssert.AreEqual
        (
            before,
            SnapshotTree(fixture.RootPath),
            "Overlap detection must precede initialization, lock creation, and all other filesystem mutations."
        );
    }

    private static async Task AssertBackupAndVerificationSucceedAsync
    (
        BackupOverlapFixture fixture,
        string sourceRoot
    )
    {
        var backup = await fixture.Synchronizer.BackupAsync(new(sourceRoot));
        Assert.IsTrue(backup.Completed, backup.Message);
        var beforeReadOnlyOperations = SnapshotTree(fixture.RootPath);

        var dryRun = await fixture.Synchronizer.BackupAsync(new(sourceRoot, DryRun: true));
        var verification = await fixture.Synchronizer.VerifyAsync(new(sourceRoot, ByteForByte: true));

        Assert.IsTrue(dryRun.Completed, dryRun.Message);
        Assert.IsTrue(verification.Completed, verification.Message);
        CollectionAssert.AreEqual(beforeReadOnlyOperations, SnapshotTree(fixture.RootPath));
    }

    private static string[] SnapshotTree(string rootPath)
    {
        var entries = new List<string>();
        var remaining = new Stack<string>();
        remaining.Push(rootPath);
        while (remaining.TryPop(out var directoryPath))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                var attributes = File.GetAttributes(path);
                var relativePath = Path.GetRelativePath(rootPath, path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    entries.Add($"link|{relativePath}|{new DirectoryInfo(path).LinkTarget}");
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    entries.Add($"directory|{relativePath}");
                    remaining.Push(path);
                }
                else
                {
                    entries.Add
                    (
                        string.Join
                        (
                            "|",
                            relativePath,
                            attributes,
                            File.GetLastWriteTimeUtc(path).Ticks,
                            Convert.ToBase64String(File.ReadAllBytes(path))
                        )
                    );
                }
            }
        }
        return entries.Order(StringComparer.Ordinal).ToArray();
    }

    private static Task WritePolicyAsync(string rootPath, string format) => File.WriteAllTextAsync
    (
        Path.Combine(rootPath, FolderPolicyFileNames.Primary),
        JsonSerializer.Serialize(new { format })
    );

    private sealed class OverlapMemoryStoreResolver(MemoryObjectStore _store) : IBackupRootStoreResolver
    {
        public const string Kind = "overlap-test-memory";

        public string StoreKind => Kind;

        public IObjectStore ResolveStore(BackupRootStore store, string descriptorRootPath) => _store;
    }

    private sealed class BackupOverlapFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-backup-overlap-";
        private readonly List<string> _directoryLinks = [];

        public BackupOverlapFixture(MemoryObjectStore? archive = null)
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddYabtFileSystemObjectStore();
            services.AddYabtMirrorFormatHandler();
            services.AddYabtZipFormatHandler();
            services.AddYabtMetadata();
            services.AddYabtSync();
            if (archive is not null)
            {
                services.AddSingleton<IBackupRootStoreResolver>(new OverlapMemoryStoreResolver(archive));
            }
            Services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public ServiceProvider Services { get; }

        public IArchiveSynchronizer Synchronizer => Services.GetRequiredService<IArchiveSynchronizer>();

        public async Task ConfigureAsync
        (
            string sourceRoot,
            string? archiveRoot,
            string format,
            ArchiveLayout? layout = null
        )
        {
            Directory.CreateDirectory(sourceRoot);
            var effectiveLayout = layout ?? ArchiveLayout.Default;
            var store = archiveRoot is null ?
                new BackupRootStore("archive", OverlapMemoryStoreResolver.Kind) :
                new BackupRootStore("archive", FileSystemObjectStoreKind.Value)
                {
                    ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["rootPath"] = JsonSerializer.SerializeToElement(archiveRoot),
                    },
                };
            var descriptor = new BackupRootDescriptor
            (
                BackupRootDescriptor.ExpectedDocumentType,
                BackupRootDescriptor.ExpectedSchemaVersion,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                effectiveLayout,
                [store]
            );
            await using (var content = File.Create(Path.Combine(sourceRoot, BackupRootFileNames.Primary)))
            {
                await Services.GetRequiredService<IBackupRootSerializer>().WriteAsync(descriptor, content);
            }
            var liveRoot = Path.Combine(sourceRoot, effectiveLayout.LivePrefix);
            Directory.CreateDirectory(liveRoot);
            await WritePolicyAsync(liveRoot, format);
            await File.WriteAllTextAsync(Path.Combine(liveRoot, "source-sentinel.txt"), "preserve source contents");
        }

        public async Task CreateDirectoryLinkAsync(string linkPath, string targetPath)
        {
            AssertInsideFixture(linkPath);
            AssertInsideFixture(targetPath);
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

        public void Dispose()
        {
            Services.Dispose();
            var fullFixturePath = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            Assert.AreEqual(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(fullFixturePath));
            Assert.IsTrue(Path.GetFileName(fullFixturePath).StartsWith(FixturePrefix, StringComparison.Ordinal));
            foreach (var linkPath in Enumerable.Reverse(_directoryLinks))
            {
                AssertInsideFixture(linkPath);
                Directory.Delete(linkPath, recursive: false);
            }
            if (Directory.Exists(fullFixturePath))
            {
                Directory.Delete(fullFixturePath, recursive: true);
            }
        }

        private void AssertInsideFixture(string path) => Assert.IsTrue
        (
            Path.GetFullPath(path).StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        );
    }
}
