using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemNamespaceTests
{
    [TestMethod]
    public void NativeNameRulesBelongToTheSelectedBackend()
    {
        var windows = new WindowsFileSystemPathBackend();
        var unix = new UnixFileSystemPathBackend();
        foreach (var name in new[] { " leading", "internal space", "\u00a0" })
        {
            windows.ValidateName(name);
            unix.ValidateName(name);
        }
        foreach (var name in new[] { "trailing ", "trailing.", "CON.txt", "COM¹", "a:b", "line\nname" })
        {
            Assert.ThrowsExactly<YabtFileSystemException>(() => windows.ValidateName(name));
            unix.ValidateName(name);
        }
        Assert.ThrowsExactly<YabtFileSystemException>(() => unix.ValidateName("literal\\backslash"));
    }

    [TestMethod]
    public void OrdinaryWindowsCollisionsAreRefusedBeforeCreatingAMissingDestination()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        var missingRoot = Path.Combine(fixture.Archive, "missing");
        var destinationNamespace = new FileSystemObjectStoreNamespace(missingRoot);

        Assert.ThrowsExactly<YabtCoreException>
        (
            () => destinationNamespace.ValidateTree
            (
                [ObjectPath.Parse("folder/Report.txt"), ObjectPath.Parse("folder/report.txt")],
                []
            )
        );
        Assert.IsFalse(Directory.Exists(missingRoot));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    public async Task ExistingSpellingAliasesAreRejectedButAFileBlockingADirectoryIsAllowed()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Archive, "Report.txt"), "untouched");
        await File.WriteAllTextAsync(Path.Combine(fixture.Archive, "blocker"), "historize later");
        var destinationNamespace = new FileSystemObjectStoreNamespace(fixture.Archive);

        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => destinationNamespace.ValidatePath(ObjectPath.Parse("report.txt"))
        );
        destinationNamespace.ValidatePath(ObjectPath.Parse("blocker/child.txt"));
        Assert.AreEqual("untouched", await File.ReadAllTextAsync(Path.Combine(fixture.Archive, "Report.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Archive, "blocker")));
    }

    [TestMethod]
    public async Task RuntimeReadsMovesAndGuardedDeletesRefuseAnotherNativeSpelling()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        var nativeFile = Path.Combine(fixture.Archive, "Report.txt");
        await File.WriteAllTextAsync(nativeFile, "untouched");
        var store = new FileSystemBackupRootStoreResolver(NullLogger<FileSystemObjectStore>.Instance)
            .ResolveSourceRoot(fixture.Archive);

        var error = await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => store.ExistsAsync("report.txt")
        );
        StringAssert.Contains(error.ToString(), "Report.txt");
        StringAssert.Contains(error.ToString(), "report.txt");
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.OpenReadAsync("report.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>(() => store.MoveAsync("report.txt", "moved.txt"));
        await Assert.ThrowsExactlyAsync<YabtFileSystemException>
        (
            () => ((IArchiveMutableObjectStore)store).TryDeleteIfContentHashMatchesAsync
            (
                "report.txt",
                ArchiveHash.Compute("untouched"u8)
            )
        );

        Assert.AreEqual("untouched", await File.ReadAllTextAsync(nativeFile));
        CollectionAssert.AreEqual(new[] { nativeFile }, Directory.GetFileSystemEntries(fixture.Archive));
    }

    [TestMethod]
    public async Task ProspectiveRemovalAllowsOnlyTheKnownFileAliasWithoutChangingRuntimeRules()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        var nativeFile = Path.Combine(fixture.Archive, "Report.txt");
        await File.WriteAllTextAsync(nativeFile, "old report");
        await File.WriteAllTextAsync(Path.Combine(fixture.Archive, "Other.txt"), "other");
        var destinationNamespace = new FileSystemObjectStoreNamespace(fixture.Archive);
        var prospective = destinationNamespace.AfterRemoving([ObjectPath.Parse("Report.txt")]);

        prospective.ValidateTree([ObjectPath.Parse("report.txt")], []);
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => prospective.ValidatePath(ObjectPath.Parse("other.txt"))
        );
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => prospective.ValidatePath(ObjectPath.Parse("report.txt/CON"))
        );
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => destinationNamespace.ValidatePath(ObjectPath.Parse("report.txt"))
        );
        Assert.AreEqual("old report", await File.ReadAllTextAsync(nativeFile));
        CollectionAssert.AreEquivalent
        (
            new[] { "Report.txt", "Other.txt" },
            Directory.GetFiles(fixture.Archive).Select(Path.GetFileName).ToArray()
        );
    }

    [TestMethod]
    public async Task ScopedProspectiveRemovalUsesTheContainingNamespaceAndPreservesOtherAliases()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        var live = Directory.CreateDirectory(Path.Combine(fixture.Archive, "live")).FullName;
        var reports = Directory.CreateDirectory(Path.Combine(live, "Reports")).FullName;
        await File.WriteAllTextAsync(Path.Combine(reports, "Old.txt"), "old report");
        await File.WriteAllTextAsync(Path.Combine(live, "Other.txt"), "other");
        var destinationNamespace = new FileSystemObjectStoreNamespace(fixture.Archive);
        var scoped = destinationNamespace.Scope(ObjectPath.Parse("live"));
        var prospective = scoped.AfterRemoving([ObjectPath.Parse("Reports")]);

        prospective.ValidateTree([ObjectPath.Parse("reports/New.txt")], []);
        Assert.IsTrue(prospective.PathComparer.Equals("reports/A", "reports/a"));
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => prospective.ValidatePath(ObjectPath.Parse("other.txt"))
        );
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => scoped.ValidatePath(ObjectPath.Parse("reports/New.txt"))
        );
        Assert.AreEqual("old report", await File.ReadAllTextAsync(Path.Combine(reports, "Old.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(reports, "New.txt")));
    }

    [TestMethod]
    public async Task RecreatedDirectoriesInheritSurvivingParentSemanticsButUntouchedDirectoriesKeepTheirOwn()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        var renamed = Directory.CreateDirectory(Path.Combine(fixture.Archive, "Reports")).FullName;
        var untouched = Directory.CreateDirectory(Path.Combine(fixture.Archive, "untouched")).FullName;
        EnableCaseSensitivity(renamed);
        EnableCaseSensitivity(untouched);
        await File.WriteAllTextAsync(Path.Combine(untouched, "blocker"), "old file");
        var destinationNamespace = new FileSystemObjectStoreNamespace(fixture.Archive);
        var prospective = destinationNamespace.AfterRemoving
        (
            [ObjectPath.Parse("Reports"), ObjectPath.Parse("untouched/blocker")]
        );

        Assert.IsFalse(destinationNamespace.PathComparer.Equals("Reports/A", "Reports/a"));
        Assert.IsTrue(prospective.PathComparer.Equals("reports/A", "reports/a"));
        Assert.AreEqual
        (
            prospective.PathComparer.GetHashCode("reports/A"),
            prospective.PathComparer.GetHashCode("REPORTS/a")
        );
        Assert.ThrowsExactly<YabtCoreException>
        (
            () => prospective.ValidateTree([ObjectPath.Parse("reports/A"), ObjectPath.Parse("reports/a")], [])
        );
        prospective.ValidateTree
        (
            [ObjectPath.Parse("untouched/blocker/A"), ObjectPath.Parse("untouched/blocker/a")],
            []
        );
        Assert.IsFalse(prospective.PathComparer.Equals("untouched/blocker/A", "untouched/blocker/a"));
        Assert.IsFalse(destinationNamespace.PathComparer.Equals("Reports/A", "Reports/a"));
        Assert.AreEqual("old file", await File.ReadAllTextAsync(Path.Combine(untouched, "blocker")));
    }

    [TestMethod]
    public void ManySiblingRemovalsDoNotCauseNativeProbesPerBlockerOrBelowRemovedFiles()
    {
        using var fixture = new FileSystemLinkFixture();
        var backend = new RootOnlyComparisonBackend(fixture.Archive);
        var destinationNamespace = new FileSystemObjectStoreNamespace(fixture.Archive, backend);
        var removals = Enumerable.Range(0, 128).Select(index => ObjectPath.Parse($"Item-{index:D3}"));
        var prospective = destinationNamespace.AfterRemoving(removals);
        backend.ComparisonQueries = 0;

        Assert.IsTrue(prospective.PathComparer.Equals("Item-127/child/Name", "item-127/CHILD/name"));
        prospective.ValidatePath(ObjectPath.Parse("item-127/child/name"));

        Assert.IsLessThan(16, backend.ComparisonQueries);
    }

    [TestMethod]
    public async Task WindowsCaseSensitivityIsReadForEachExistingSubtreeAndInheritedDirectory()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows directory semantics test."); }
        using var fixture = new FileSystemLinkFixture();
        var sensitive = Directory.CreateDirectory(Path.Combine(fixture.Archive, "sensitive")).FullName;
        var ordinary = Directory.CreateDirectory(Path.Combine(fixture.Archive, "ordinary")).FullName;
        EnableCaseSensitivity(sensitive);
        var destinationNamespace = new FileSystemObjectStoreNamespace(fixture.Archive);

        Assert.IsFalse(destinationNamespace.PathComparer.Equals("sensitive/A", "sensitive/a"));
        Assert.IsTrue(destinationNamespace.PathComparer.Equals("ordinary/A", "ordinary/a"));
        Assert.IsTrue(destinationNamespace.PathComparer.Equals("SENSITIVE", "sensitive"));
        Assert.IsFalse(destinationNamespace.Scope(ObjectPath.Parse("sensitive")).PathComparer.Equals("A", "a"));
        Assert.IsFalse(destinationNamespace.PathComparer.Equals("sensitive/new/A", "sensitive/new/a"));
        var inherited = Directory.CreateDirectory(Path.Combine(sensitive, "new")).FullName;
        Assert.IsFalse(FileSystemPathBackend.Current.GetDirectoryNameComparer(inherited).Equals("A", "a"));

        await File.WriteAllTextAsync(Path.Combine(sensitive, "A"), "upper");
        await File.WriteAllTextAsync(Path.Combine(sensitive, "a"), "lower");
        destinationNamespace.ValidateTree([ObjectPath.Parse("sensitive/A"), ObjectPath.Parse("sensitive/a")], []);
        Assert.AreEqual("upper", await File.ReadAllTextAsync(Path.Combine(sensitive, "A")));
        Assert.AreEqual("lower", await File.ReadAllTextAsync(Path.Combine(sensitive, "a")));
        Assert.IsFalse(FileSystemObjectStoreNamespace.NativePathsEqual(Path.Combine(sensitive, "A"), Path.Combine(sensitive, "a")));
        Assert.IsTrue(FileSystemObjectStoreNamespace.IsNativePathUnder(Path.Combine(ordinary, "file"), ordinary));
    }

    private static void EnableCaseSensitivity(string directory)
    {
        var start = new ProcessStartInfo("fsutil.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("file");
        start.ArgumentList.Add("setCaseSensitiveInfo");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("enable");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("fsutil did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive($"This host cannot enable directory case sensitivity: {output} {error}");
        }
    }

    private sealed class RootOnlyComparisonBackend(string rootPath) : FileSystemPathBackend
    {
        public int ComparisonQueries { get; set; }

        public override StringComparison NativePathComparison => StringComparison.OrdinalIgnoreCase;

        public override void ValidateName(string name) { }

        public override StringComparer GetDirectoryNameComparer(string directoryPath)
        {
            Assert.AreEqual(rootPath, directoryPath, "Removed file descendants must not be probed as directories.");
            ComparisonQueries++;
            return StringComparer.OrdinalIgnoreCase;
        }
    }
}
