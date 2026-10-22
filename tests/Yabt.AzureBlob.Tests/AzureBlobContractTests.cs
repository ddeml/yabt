using Azure;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class AzureBlobContractTests
{
    [TestMethod]
    public async Task FolderLocalTraversalPreservesExactNamesAcrossPages()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        await ObjectStoreContract.VerifyFolderLocalTraversalAsync(fixture.Store);
        Assert.IsTrue(fixture.Server.Requests.Any(request => request.Target.Contains("marker=2", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConditionalMutationsPreserveMismatchedAndMissingObjects()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        await ObjectStoreContract.VerifyConditionalMutationsAsync(fixture.Store);
    }

    [TestMethod]
    public async Task WholeFolderMovePreservesMarkersAndSiblingPrefixes()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        await ObjectStoreContract.VerifyWholeFolderMoveAsync(fixture.Store);
        var copies = fixture.Server.Requests.Where(request => request.Headers.ContainsKey("x-ms-copy-source")).ToArray();
        Assert.HasCount(3, copies);
        Assert.IsTrue(copies.All(request => request.Headers["If-None-Match"] == "*"));
        Assert.IsTrue(copies.All(request => request.Headers.ContainsKey("x-ms-source-if-match")));
    }

    [TestMethod]
    public async Task UploadIsCreateIfAbsent()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        await ObjectStoreContract.VerifyCreateIfAbsentAsync(fixture.Store);
    }

    [TestMethod]
    public async Task FailedSourceReadLeavesNoPartialFinalObject()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        await ObjectStoreContract.VerifyFailedUploadAsync(fixture.Store);
    }

    [TestMethod]
    public async Task ArchiveLockSerializesOwnersAndCanBeReacquired()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        await ObjectStoreContract.VerifyLockContentionAndReacquisitionAsync(fixture.Store);
    }

    [TestMethod]
    public async Task LockReleaseDetectsChangedOwnerAndPreservesReplacement()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        var archiveLock = await ((IArchiveMutableObjectStore)fixture.Store).AcquireArchiveMutationLockAsync();
        var lost = archiveLock.LockLostToken;
        fixture.SetContent(ArchiveInternalObjectKeys.MutationLock, "replacement owner");

        await Assert.ThrowsExactlyAsync<YabtCoreException>(() => archiveLock.DisposeAsync().AsTask());

        Assert.IsTrue(lost.IsCancellationRequested);
        Assert.AreEqual("replacement owner", fixture.GetContent(ArchiveInternalObjectKeys.MutationLock));
    }

    [TestMethod]
    public async Task ExactObjectAndSameNamedPrefixRemainDistinct()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        fixture.SetContent("node", "exact object");
        fixture.SetContent("node/file.txt", "descendant");
        var items = new List<ArchiveFolderItem>();
        await foreach (var item in fixture.Store.GetFolderItemsAsync(null))
        {
            items.Add(item);
        }

        Assert.HasCount(2, items);
        Assert.AreEqual("node", items.Single(item => item.IsObject).Key);
        Assert.AreEqual("node", items.Single(item => item.IsFolder).Key);
        await fixture.Store.MoveFolderAsync("node", "history/node");
        Assert.AreEqual("exact object", fixture.GetContent("node"));
        Assert.AreEqual("descendant", fixture.GetContent("history/node/file.txt"));
        Assert.IsNull(fixture.GetContent("node/file.txt"));
    }

    [TestMethod]
    [DataRow("PUT")]
    [DataRow("DELETE")]
    public async Task ConditionalMutationRejectsChangeAfterHashRead(string method)
    {
        await using var fixture = new AzureBlobProtocolFixture();
        fixture.SetContent("race.txt", "original");
        fixture.Intercept = request =>
        {
            if (request.Method == method)
            {
                fixture.SetContent("race.txt", "concurrent");
                fixture.Intercept = null;
            }
            return null;
        };
        var hash = ArchiveHash.Compute("original"u8);
        using var replacement = new MemoryStream("replacement"u8.ToArray());
        var changed = method == "PUT" ?
            await fixture.Store.TryReplaceIfContentHashMatchesAsync("race.txt", hash, replacement, "text/plain", new Dictionary<string, string>()) :
            await fixture.Store.TryDeleteIfContentHashMatchesAsync("race.txt", hash);

        Assert.IsFalse(changed);
        Assert.AreEqual("concurrent", fixture.GetContent("race.txt"));
        var mutation = fixture.Server.Requests.Single(request => request.Method == method);
        Assert.AreEqual("\"version-1\"", mutation.Headers["If-Match"]);
    }

    [TestMethod]
    public async Task FailedCopyAndFallbackUploadPreserveSource()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        fixture.SetContent("source.txt", "original");
        fixture.Intercept = request => request.Method == "PUT" ?
            AzureBlobProtocolFixture.Error(503, "ServerBusy") : null;

        await Assert.ThrowsExactlyAsync<YabtAzureBlobException>
        (
            () => fixture.Store.MoveAsync("source.txt", "destination.txt")
        );

        Assert.AreEqual("original", fixture.GetContent("source.txt"));
        Assert.IsNull(fixture.GetContent("destination.txt"));
        Assert.IsFalse(fixture.Server.Requests.Any(request => request.Method == "DELETE"));
        Assert.HasCount(2, fixture.Server.Requests.Where(request => request.Method == "PUT").ToArray());
    }

    [TestMethod]
    public async Task UnsupportedServerSideCopyFallsBackToGuardedStreamingMove()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        fixture.SetContent("source.txt", "original");
        fixture.Intercept = request => request.Headers.ContainsKey("x-ms-copy-source") ?
            AzureBlobProtocolFixture.Error(501, "NotImplemented") : null;

        await fixture.Store.MoveAsync("source.txt", "destination.txt");

        Assert.IsNull(fixture.GetContent("source.txt"));
        Assert.AreEqual("original", fixture.GetContent("destination.txt"));
        var requests = fixture.Server.Requests;
        Assert.AreEqual("\"version-1\"", requests.Single(request => request.Method == "GET").Headers["If-Match"]);
        Assert.AreEqual("\"version-1\"", requests.Single(request => request.Method == "DELETE").Headers["If-Match"]);
        Assert.AreEqual
        (
            "*",
            requests.Single(request => request.Method == "PUT" && request.Target.Contains("comp=blocklist", StringComparison.Ordinal)).Headers["If-None-Match"]
        );
    }

    [TestMethod]
    public async Task FailedSourceDeletionLeavesCompleteCopyAndOriginal()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        fixture.SetContent("source.txt", "original");
        fixture.Intercept = request => request.Method == "DELETE" ?
            AzureBlobProtocolFixture.Error(503, "ServerBusy") : null;

        await Assert.ThrowsExactlyAsync<YabtAzureBlobException>
        (
            () => fixture.Store.MoveAsync("source.txt", "destination.txt")
        );

        Assert.AreEqual("original", fixture.GetContent("source.txt"));
        Assert.AreEqual("original", fixture.GetContent("destination.txt"));
        Assert.AreEqual
        (
            "\"version-1\"",
            fixture.Server.Requests.Single(request => request.Method == "DELETE").Headers["If-Match"]
        );
    }

    [TestMethod]
    public async Task FailedContinuationNeverBecomesASuccessfulTruncatedListing()
    {
        await using var fixture = new AzureBlobProtocolFixture();
        fixture.SetContent("folder/a.txt", "a");
        fixture.SetContent("folder/b.txt", "b");
        fixture.SetContent("folder/c.txt", "c");
        fixture.Intercept = request => request.Target.Contains("marker=2", StringComparison.Ordinal) ?
            AzureBlobProtocolFixture.Error(503, "ServerBusy") : null;
        var observed = new List<string>();

        await Assert.ThrowsExactlyAsync<RequestFailedException>(async () =>
        {
            await foreach (var item in fixture.Store.GetFolderItemsAsync("folder"))
            {
                observed.Add(item.Key);
            }
        });

        CollectionAssert.AreEqual(new[] { "folder/a.txt", "folder/b.txt" }, observed);
    }
}
