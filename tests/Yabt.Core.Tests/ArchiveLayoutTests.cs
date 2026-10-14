using Yabt.Core.Models;

namespace Yabt.Core.Tests;

[TestClass]
public sealed class ArchiveLayoutTests
{
    [TestMethod]
    [DataRow(" documents/report.txt")]
    [DataRow("documents /report.txt ")]
    [DataRow(" ")]
    [DataRow("\t/line\nbreak.txt")]
    [DataRow("\u00a0/\u00a0notes\u00a0")]
    public void ObjectKeysAndFolderItemsPreserveExactNames(string key)
    {
        Assert.AreEqual(key, ArchiveLayout.NormalizeObjectKey(key));
        Assert.AreEqual(key, ArchiveLayout.NormalizeObjectPrefix(key));
        Assert.AreEqual(key, ArchiveFolderItem.CreateFolder("name", key).Key);

        var item = ArchiveFolderItem.CreateObject("name", new(key, 12));
        Assert.AreEqual(key, item.Key);
        Assert.AreEqual(key, item.Object!.Key);
    }

    [TestMethod]
    public void WhitespacePathsRemainDistinctFromRootAndUnspacedSiblings()
    {
        var layout = new ArchiveLayout(" live ", " history ");

        Assert.AreEqual(" live / file.txt ", layout.ToLiveObjectKey(" file.txt "));
        Assert.AreEqual(" history / ", layout.ToHistoryObjectKey(" "));
        Assert.AreEqual(" ", ArchiveLayout.RemovePrefix("folder/ ", "folder"));
        Assert.IsTrue(ArchiveLayout.IsUnderPrefix(" documents/file", " documents"));
        Assert.IsFalse(ArchiveLayout.IsUnderPrefix(" documents/file", "documents"));
        Assert.IsFalse(ArchiveLayout.IsUnderPrefix("documents/file", " documents"));
        Assert.ThrowsExactly<ArgumentException>(() => ArchiveLayout.RemovePrefix(" documents/file", "documents"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void OnlyNullAndEmptyPathsRepresentRoot(string? key)
    {
        Assert.AreEqual(string.Empty, ArchiveLayout.NormalizeObjectKey(key));
        Assert.IsNull(ArchiveLayout.NormalizeObjectPrefix(key));
        Assert.AreEqual("name", ArchiveLayout.CombinePrefixAndRelativePath(key, "name"));
    }

    [TestMethod]
    public void NativeBackslashSeparatorsAreRejectedAtTheCanonicalPathBoundary()
    {
        Assert.ThrowsExactly<ArgumentException>
        (
            () => ArchiveLayout.NormalizeObjectKey(" directory \\ file.txt ")
        );
    }

    [TestMethod]
    [DataRow("/rooted")]
    [DataRow("trailing/")]
    [DataRow("double//separator")]
    [DataRow("double\\\\separator")]
    [DataRow("\\rooted")]
    [DataRow("\\\\server\\share")]
    [DataRow("C:/rooted")]
    [DataRow("C:drive-relative")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("folder/./file")]
    [DataRow("folder/../file")]
    [DataRow("nul\0name")]
    public void AmbiguousPathsAreRejectedInsteadOfChangingIdentity(string key)
    {
        Assert.ThrowsExactly<ArgumentException>(() => ArchiveLayout.NormalizeObjectKey(key));
        Assert.ThrowsExactly<ArgumentException>(() => ArchiveLayout.NormalizeObjectPrefix(key));
        Assert.ThrowsExactly<ArgumentException>(() => ArchiveLayout.CombinePrefixAndRelativePath("folder", key));
    }
}
