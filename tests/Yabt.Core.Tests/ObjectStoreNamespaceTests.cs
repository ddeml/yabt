using System.Globalization;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Core.Tests;

[TestClass]
public sealed class ObjectStoreNamespaceTests
{
    [TestMethod]
    public void DefaultStoreNamespacePreservesDistinctCanonicalNames()
    {
        IReadOnlyObjectStore store = new DefaultNamespaceObjectStore();
        var paths = new[] { "File", "file", "résumé", "resume", " file", "file " };

        Assert.AreSame(ObjectStoreNamespace.Ordinal, store.Namespace);
        store.Namespace.ValidateTree(paths.Select(ObjectPath.Parse), []);
        Assert.IsFalse(store.Namespace.PathComparer.Equals("File", "file"));
    }

    [TestMethod]
    [DataRow("File", "file")]
    [DataRow("résumé", "resume")]
    [DataRow("résumé", "re\u0301sume\u0301")]
    public void NamespaceComparerDetectsFileAliases(string first, string second)
    {
        var namespaceRules = CreateFoldingNamespace();

        var exception = Assert.ThrowsExactly<YabtCoreException>
        (
            () => namespaceRules.ValidateTree([ObjectPath.Parse(first), ObjectPath.Parse(second)], [])
        );

        StringAssert.Contains(exception.Message, first);
        StringAssert.Contains(exception.Message, second);
    }

    [TestMethod]
    public void NamespaceComparerDetectsAliasesInImplicitAncestorDirectories()
    {
        var namespaceRules = CreateFoldingNamespace();

        var exception = Assert.ThrowsExactly<YabtCoreException>
        (
            () => namespaceRules.ValidateTree
            (
                [ObjectPath.Parse("résumé/first.txt"), ObjectPath.Parse("resume/second.txt")],
                []
            )
        );

        StringAssert.Contains(exception.Message, "résumé");
        StringAssert.Contains(exception.Message, "resume");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileAndImplicitDirectoryCollisionsAreIndependentOfInputOrder(bool childFirst)
    {
        var files = new[] { ObjectPath.Parse("Folder"), ObjectPath.Parse("folder/child.txt") };
        var namespaceRules = CreateFoldingNamespace();

        var exception = Assert.ThrowsExactly<YabtCoreException>
        (
            () => namespaceRules.ValidateTree(childFirst ? files.Reverse() : files, [])
        );

        StringAssert.Contains(exception.Message, "Folder");
        StringAssert.Contains(exception.Message, "folder");
    }

    [TestMethod]
    public void ExplicitDirectoriesShareExactAncestorsButCannotAliasOtherNamesOrFiles()
    {
        var namespaceRules = CreateFoldingNamespace();
        namespaceRules.ValidateTree
        (
            [ObjectPath.Parse("Folder/first.txt"), ObjectPath.Parse("Folder/second.txt")],
            [ObjectPath.Parse("Folder"), ObjectPath.Parse("Folder"), ObjectPath.Root]
        );

        Assert.ThrowsExactly<YabtCoreException>
        (
            () => namespaceRules.ValidateTree
            (
                [ObjectPath.Parse("Folder/first.txt")],
                [ObjectPath.Parse("folder")]
            )
        );
        Assert.ThrowsExactly<YabtCoreException>
        (
            () => namespaceRules.ValidateTree([ObjectPath.Parse("File")], [ObjectPath.Parse("file")])
        );
    }

    [TestMethod]
    public void TreeValidationAppliesProviderRulesToImplicitAncestors()
    {
        var validated = new List<ObjectPath>();
        var namespaceRules = new TestObjectStoreNamespace
        (
            StringComparer.Ordinal,
            path =>
            {
                validated.Add(path);
                if (path.Value == "blocked")
                {
                    throw new YabtCoreException("Destination directory 'blocked' is not representable.");
                }
            }
        );

        Assert.ThrowsExactly<YabtCoreException>
        (
            () => namespaceRules.ValidateTree([ObjectPath.Parse("blocked/child.txt")], [])
        );
        CollectionAssert.Contains(validated, ObjectPath.Parse("blocked/child.txt"));
        CollectionAssert.Contains(validated, ObjectPath.Parse("blocked"));
        Assert.ThrowsExactly<YabtCoreException>(() => namespaceRules.Scope(ObjectPath.Parse("blocked/child")));
    }

    [TestMethod]
    public void ScopedNamespaceUsesParentContextForComparisonValidationAndFurtherScopes()
    {
        var validated = new List<ObjectPath>();
        var namespaceRules = new TestObjectStoreNamespace(new ContextualPathComparer(), validated.Add);
        var scope = namespaceRules.Scope(ObjectPath.Parse("folded"));
        var nestedScope = scope.Scope(ObjectPath.Parse("nested"));

        Assert.AreSame(namespaceRules, namespaceRules.Scope(ObjectPath.Root));
        Assert.IsFalse(namespaceRules.PathComparer.Equals("File", "file"));
        Assert.IsTrue(scope.PathComparer.Equals("File", "file"));
        Assert.AreEqual(scope.PathComparer.GetHashCode("File"), scope.PathComparer.GetHashCode("file"));
        Assert.AreEqual(0, nestedScope.PathComparer.Compare("File", "file"));
        nestedScope.ValidatePath(ObjectPath.Parse("name"));
        CollectionAssert.Contains(validated, ObjectPath.Parse("folded/nested/name"));

        Assert.ThrowsExactly<YabtCoreException>
        (
            () => scope.ValidateTree([ObjectPath.Parse("Folder/a"), ObjectPath.Parse("folder/b")], [])
        );
    }

    [TestMethod]
    public void TreeValidationRejectsRootFilesAndDuplicateFileDeclarations()
    {
        Assert.ThrowsExactly<YabtCoreException>
        (
            () => ObjectStoreNamespace.Ordinal.ValidateTree([ObjectPath.Root], [])
        );
        Assert.ThrowsExactly<YabtCoreException>
        (
            () => ObjectStoreNamespace.Ordinal.ValidateTree([ObjectPath.Parse("file"), ObjectPath.Parse("file")], [])
        );
    }

    private static TestObjectStoreNamespace CreateFoldingNamespace() => new
    (
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)
    );

    private sealed class TestObjectStoreNamespace
    (
        StringComparer _pathComparer,
        Action<ObjectPath>? _validatePath = default
    ) : ObjectStoreNamespace
    {
        public override StringComparer PathComparer => _pathComparer;

        public override void ValidatePath(ObjectPath path) => _validatePath?.Invoke(path);
    }

    private sealed class ContextualPathComparer : StringComparer
    {
        public override int Compare(string? x, string? y) =>
            StringComparer.Ordinal.Compare(Normalize(x), Normalize(y));

        public override bool Equals(string? x, string? y) =>
            StringComparer.Ordinal.Equals(Normalize(x), Normalize(y));

        public override int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Normalize(obj)!);

        private static string? Normalize(string? path) =>
            path?.StartsWith("folded/", StringComparison.Ordinal) == true ? path.ToUpperInvariant() : path;
    }

    private sealed class DefaultNamespaceObjectStore : IReadOnlyObjectStore
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ArchiveObjectContent> OpenReadAsync
        (
            string key,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
        (
            string? folderPrefix,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
