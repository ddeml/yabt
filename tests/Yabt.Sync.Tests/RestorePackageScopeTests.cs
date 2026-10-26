using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Sync.Implementation;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RestorePackageScopeTests
{
    [TestMethod]
    [DataRow("elsewhere/file.txt")]
    [DataRow("FOLDER/file.txt")]
    [DataRow("folder")]
    public async Task PackageCannotClaimForeignOrCaseEquivalentOutput(string outputPath)
    {
        using var index = TemporaryIndex.BeginOperation();
        await using var plan = CreatePlan(outputPath, ["folder"]);

        var exception = Assert.ThrowsExactly<YabtSyncException>
        (
            () => RestorePackageScope.Validate(plan, "folder")
        );

        StringAssert.Contains(exception.Message, "outside its owning logical folder");
    }

    [TestMethod]
    public async Task PackageMayRetainImplicitAncestorsAndItsOwnDescendants()
    {
        using var index = TemporaryIndex.BeginOperation();
        await using var plan = CreatePlan
        (
            "parent/folder/child/file.txt",
            ["parent", "parent/folder", "parent/folder/child"]
        );

        RestorePackageScope.Validate(plan, "parent/folder");
    }

    [TestMethod]
    public async Task PackageCannotCreateForeignEmptyDirectory()
    {
        using var index = TemporaryIndex.BeginOperation();
        await using var plan = CreatePlan("folder/file.txt", ["folder", "foreign"]);

        Assert.ThrowsExactly<YabtSyncException>(() => RestorePackageScope.Validate(plan, "folder"));
    }

    private static RestorePlan CreatePlan(string filePath, string[] directoryPaths)
    {
        var file = new ArchiveProjectedObject
        (
            filePath,
            _ => throw new InvalidOperationException("Scope preflight must never open payloads."),
            ContentLength: 0,
            ContentHash: ArchiveHash.Compute([])
        );
        return new
        (
            [file],
            RestoreDirectoryIndex.Create(directoryPaths, new HashSet<string>(), StringComparer.Ordinal),
            [],
            new Dictionary<string, IReadOnlyList<ArchiveRestoreProjection>>(),
            ObjectStoreNamespace.Ordinal
        );
    }
}
