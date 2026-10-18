using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemObjectPathComparerTests
{
    [TestMethod]
    public void ExactAncestorSegmentsDoNotRequireFilesystemComparisonEvidence()
    {
        var root = Path.GetFullPath("unsupported-ancestor");
        var mountedDirectory = Path.Combine(root, "mounted");
        var backend = new RecordingPathComparisonBackend
        (
            new Dictionary<string, StringComparer>(StringComparer.Ordinal)
            {
                [mountedDirectory] = StringComparer.Ordinal,
            }
        );
        var comparer = new FileSystemObjectPathComparer(root, backend);

        Assert.IsTrue(comparer.Compare("mounted/source", "mounted/archive") > 0);

        CollectionAssert.AreEqual(new[] { mountedDirectory }, backend.QueriedDirectories);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DifferentlySpelledComponentsEachUseTheirContainingDirectorySemantics(bool caseSensitiveLeaf)
    {
        var root = Path.GetFullPath("unsupported-ancestor");
        var mountedDirectory = Path.Combine(root, "mounted");
        var leafParent = Path.Combine(mountedDirectory, "Folded", "shared");
        var backend = new RecordingPathComparisonBackend
        (
            new Dictionary<string, StringComparer>(StringComparer.Ordinal)
            {
                [mountedDirectory] = StringComparer.OrdinalIgnoreCase,
                [leafParent] = caseSensitiveLeaf ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase,
            }
        );
        var comparer = new FileSystemObjectPathComparer(root, backend);

        var comparison = comparer.Compare("mounted/Folded/shared/Leaf", "mounted/folded/shared/leaf");

        Assert.AreEqual(caseSensitiveLeaf, comparison != 0);
        CollectionAssert.AreEqual(new[] { mountedDirectory, leafParent }, backend.QueriedDirectories);
    }

    [TestMethod]
    public void UnknownSemanticsForDifferentlySpelledComponentsStillFail()
    {
        var root = Path.GetFullPath("unsupported-ancestor");
        var backend = new RecordingPathComparisonBackend(new Dictionary<string, StringComparer>());
        var comparer = new FileSystemObjectPathComparer(root, backend);

        Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => comparer.Compare("mounted/source", "mounted/archive")
        );

        CollectionAssert.AreEqual(new[] { Path.Combine(root, "mounted") }, backend.QueriedDirectories);
    }

    private sealed class RecordingPathComparisonBackend
    (
        IReadOnlyDictionary<string, StringComparer> _directoryComparers
    ) : FileSystemPathBackend
    {
        public List<string> QueriedDirectories { get; } = [];

        public override StringComparison NativePathComparison => StringComparison.Ordinal;

        public override void ValidateName(string name)
        {
        }

        public override StringComparer GetDirectoryNameComparer(string directoryPath)
        {
            QueriedDirectories.Add(directoryPath);
            return _directoryComparers.TryGetValue(directoryPath, out var comparer) ? comparer :
                throw new YabtFileSystemException("This ancestor namespace cannot be inspected.", path: directoryPath);
        }
    }
}
