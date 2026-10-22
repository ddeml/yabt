using System.IO.Hashing;
using Yabt.Core.Models;

namespace Yabt.Core.Tests;

[TestClass]
public sealed class ArchiveGeneratedHashTests
{
    [TestMethod]
    public void GeneratedContentHashMatchesIncrementalHashWithoutRetainingTheContent()
    {
        var block = new byte[64 * 1024];
        new Random(17).NextBytes(block);
        var expected = new XxHash128();
        for (var index = 0; index < 1024; index++) { expected.Append(block); }

        _ = ArchiveHash.Compute(stream => stream.Write(block));
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var actual = ArchiveHash.Compute(stream =>
        {
            for (var index = 0; index < 1024; index++) { stream.Write(block); }
            stream.Flush();
        });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.AreEqual(ArchiveHash.Format(expected.GetHashAndReset()), actual);
        Assert.IsLessThan(256 * 1024, allocated);
    }

    [TestMethod]
    public void GeneratedContentHashPreservesWriteFailures()
    {
        var expected = new IOException("Serialization failed.");

        var actual = Assert.ThrowsExactly<IOException>
        (
            () => ArchiveHash.Compute(stream =>
            {
                stream.WriteByte(42);
                throw expected;
            })
        );

        Assert.AreSame(expected, actual);
    }
}
