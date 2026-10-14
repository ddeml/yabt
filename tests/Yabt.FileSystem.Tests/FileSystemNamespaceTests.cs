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
}
