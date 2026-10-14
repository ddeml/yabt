using Yabt.Core.Models;

namespace Yabt.Core.Tests;

[TestClass]
public sealed class ObjectPathTests
{
    [TestMethod]
    public void DefaultNullAndEmptyPathsAreTheSameRoot()
    {
        ObjectPath empty = default;

        Assert.AreEqual(ObjectPath.Root, empty);
        Assert.AreEqual(ObjectPath.Root, ObjectPath.Parse(null));
        Assert.AreEqual(ObjectPath.Root, ObjectPath.Parse(string.Empty));
        Assert.AreEqual(ObjectPath.Root.GetHashCode(), ObjectPath.Parse(string.Empty).GetHashCode());
        Assert.AreEqual(string.Empty, empty.Value);
        Assert.AreEqual(string.Empty, empty.Name);
        Assert.IsTrue(empty.IsRoot);
        Assert.AreEqual(ObjectPath.Root, empty.Parent);
    }

    [TestMethod]
    [DataRow(" folder / file.txt ")]
    [DataRow(" ")]
    [DataRow("\n/\t")]
    [DataRow("\u00a0folder/file\u00a0")]
    [DataRow("folder/C:name")]
    public void CanonicalNamesRoundTripWithoutNativePathConversion(string value)
    {
        var path = ObjectPath.Parse(value);

        Assert.AreEqual(value, path.Value);
        Assert.AreEqual(value, path.ToString());
        Assert.IsFalse(path.IsRoot);
    }

    [TestMethod]
    public void EqualityDoesNotFoldCaseAccentsOrWhitespace()
    {
        var paths = new HashSet<ObjectPath>
        {
            ObjectPath.Parse("File"),
            ObjectPath.Parse("file"),
            ObjectPath.Parse("résumé"),
            ObjectPath.Parse("resume"),
            ObjectPath.Parse("re\u0301sume\u0301"),
            ObjectPath.Parse(" file"),
            ObjectPath.Parse("file "),
        };

        Assert.HasCount(7, paths);
        Assert.AreEqual(ObjectPath.Parse("file"), ObjectPath.Parse("file"));
        Assert.AreEqual
        (
            StringComparer.Ordinal.Compare("File", "file"),
            ObjectPath.Parse("File").CompareTo(ObjectPath.Parse("file"))
        );
    }

    [TestMethod]
    public void ParentCombinationAndContainmentRespectExactSegmentBoundaries()
    {
        var parent = ObjectPath.Parse(" Folder ");
        var child = ObjectPath.Parse(" child /file ");
        var combined = parent.Combine(child);

        Assert.AreEqual(" Folder / child /file ", combined.Value);
        Assert.AreEqual("file ", combined.Name);
        Assert.AreEqual(ObjectPath.Parse(" Folder / child "), combined.Parent);
        Assert.AreEqual(parent, combined.Parent.Parent);
        Assert.AreEqual(ObjectPath.Root, parent.Parent);
        Assert.AreEqual(parent, parent.Combine(ObjectPath.Root));
        Assert.AreEqual(child, ObjectPath.Root.Combine(child));
        Assert.IsTrue(combined.IsUnder(parent));
        Assert.IsTrue(parent.IsUnder(parent));
        Assert.IsTrue(parent.IsUnder(ObjectPath.Root));
        Assert.IsFalse(ObjectPath.Root.IsUnder(parent));
        Assert.IsFalse(combined.IsUnder(ObjectPath.Parse(" Folder")));
        Assert.IsFalse(combined.IsUnder(ObjectPath.Parse(" folder ")));
        Assert.IsFalse(ObjectPath.Parse("folder-more/file").IsUnder(ObjectPath.Parse("folder")));
    }

    [TestMethod]
    [DataRow("folder\\file")]
    [DataRow("/rooted")]
    [DataRow("//server/share")]
    [DataRow("C:/rooted")]
    [DataRow("C:relative")]
    [DataRow("folder//file")]
    [DataRow("folder/")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("folder/./file")]
    [DataRow("folder/../file")]
    [DataRow("nul\0name")]
    public void CanonicalParserRejectsAmbiguousAndNativePaths(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => ObjectPath.Parse(value));
    }
}
