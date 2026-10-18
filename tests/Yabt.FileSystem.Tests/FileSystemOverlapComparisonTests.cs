using System.Diagnostics.CodeAnalysis;
using Yabt.Core.Models;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemOverlapComparisonTests
{
    [TestMethod]
    public void UnknownAncestorComparisonIsAllowedOnlyForBestEffortContainment()
    {
        var backend = new ComparisonBackend();
        var candidate = NativePath("first", "source");
        var ancestor = NativePath("second", "archive");

        Assert.IsFalse
        (
            FileSystemObjectStoreNamespace.IsNativePathUnder
            (
                candidate, ancestor, allowUnknownComparison: true, backend
            )
        );
        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => FileSystemObjectStoreNamespace.IsNativePathUnder(candidate, ancestor, backend: backend)
        );

        var objectNamespace = new FileSystemObjectStoreNamespace(candidate, backend);
        Assert.ThrowsExactly<YabtFileSystemException>(() => objectNamespace.ValidatePath(ObjectPath.Root));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExactContainmentNeedsNoAncestorComparison(bool allowUnknownComparison)
    {
        var backend = new ComparisonBackend(_failure: new IOException("Comparison must not be requested."));
        var ancestor = NativePath("mount", "source");
        var candidate = Path.Combine(ancestor, "child");

        Assert.IsTrue
        (
            FileSystemObjectStoreNamespace.IsNativePathUnder
            (
                candidate, ancestor, allowUnknownComparison, backend
            )
        );
        Assert.IsTrue
        (
            FileSystemObjectStoreNamespace.IsNativePathUnder
            (
                ancestor, ancestor, allowUnknownComparison, backend
            )
        );
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void KnownComparisonRemainsAuthoritative(bool ignoreCase, bool allowUnknownComparison)
    {
        var backend = new ComparisonBackend(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var candidate = NativePath("mount", "SOURCE", "child");
        var ancestor = NativePath("mount", "source");

        Assert.AreEqual
        (
            ignoreCase,
            FileSystemObjectStoreNamespace.IsNativePathUnder
            (
                candidate, ancestor, allowUnknownComparison, backend
            )
        );
        Assert.IsFalse
        (
            FileSystemObjectStoreNamespace.IsNativePathUnder
            (
                NativePath("mount", "source-other"), ancestor, allowUnknownComparison, backend
            )
        );
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ComparisonAccessFailuresAreNeverUnknown(bool allowUnknownComparison)
    {
        var failure = new YabtFileSystemException
        (
            "Cannot inspect directory semantics.",
            new UnauthorizedAccessException("Directory access denied.")
        );
        var backend = new ComparisonBackend(_failure: failure);

        var exception = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => FileSystemObjectStoreNamespace.IsNativePathUnder
            (
                NativePath("first"), NativePath("second"), allowUnknownComparison, backend
            )
        );

        Assert.AreSame(failure, exception);
    }

    private static string NativePath(string first, string? second = null, string? third = null)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var path = Path.Combine(root, first);
        if (second is not null) { path = Path.Combine(path, second); }
        return third is null ? path : Path.Combine(path, third);
    }

    private sealed class ComparisonBackend
    (
        StringComparer? _comparer = null,
        Exception? _failure = null
    ) : FileSystemPathBackend
    {
        public override StringComparison NativePathComparison => StringComparison.Ordinal;

        public override void ValidateName(string name) { }

        public override StringComparer GetDirectoryNameComparer(string directoryPath) =>
            TryGetDirectoryNameComparer(directoryPath, out var comparer) ? comparer :
                throw new YabtFileSystemException("Unsupported ancestor namespace.", path: directoryPath);

        public override bool TryGetDirectoryNameComparer
        (
            string directoryPath,
            [NotNullWhen(true)] out StringComparer? comparer
        )
        {
            if (_failure is not null) { throw _failure; }
            comparer = _comparer;
            return comparer is not null;
        }
    }
}
