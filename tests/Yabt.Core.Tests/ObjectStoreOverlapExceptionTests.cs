namespace Yabt.Core.Tests;

[TestClass]
public sealed class ObjectStoreOverlapExceptionTests
{
    [TestMethod]
    public void RecognizesOverlapInsideContextAndNonfirstAggregateBranches()
    {
        var overlap = new ObjectStoreOverlapException("Source and archive overlap.");
        var contextualFailure = new IOException("Reading a source object failed.", overlap);
        var aggregate = new AggregateException
        (
            new IOException("An unrelated source read failed."),
            new InvalidOperationException("Building the package failed.", contextualFailure)
        );

        Assert.IsTrue(ObjectStoreOverlapException.IsContainedIn(overlap));
        Assert.IsTrue(ObjectStoreOverlapException.IsContainedIn(contextualFailure));
        Assert.IsTrue(ObjectStoreOverlapException.IsContainedIn(aggregate));
        Assert.IsTrue(ObjectStoreOverlapException.IsContainedIn(new IOException("Backup failed.", aggregate)));
    }

    [TestMethod]
    public void OrdinarySourceFailuresRemainRecoverable()
    {
        var exception = new IOException
        (
            "Source read failed.",
            new AggregateException
            (
                new IOException("The message mentions overlap but contains no overlap evidence."),
                new UnauthorizedAccessException("Access was denied.")
            )
        );

        Assert.IsFalse(ObjectStoreOverlapException.IsContainedIn(exception));
    }
}
