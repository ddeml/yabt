using System.Text;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;
using Yabt.WebDav.Implementation;

namespace Yabt.WebDav.Tests;

[TestClass]
public sealed class WebDavProviderContractTests
{
    [TestMethod]
    public async Task FolderTraversalPreservesExactImmediateNames()
    {
        await using var server = new WebDavContractServer();

        await ObjectStoreContract.VerifyFolderLocalTraversalAsync(server.Store);

        Assert.IsTrue(server.Requests.Where(request => request.Method == "PROPFIND").All
        (
            request => request.Headers["Depth"] is "0" or "1"
        ));
    }

    [TestMethod]
    public async Task ConditionalMutationsRequireTheCurrentBytes()
    {
        await using var server = new WebDavContractServer();

        await ObjectStoreContract.VerifyConditionalMutationsAsync(server.Store);

        Assert.IsTrue(server.Requests.Where(request => request.Method == "DELETE").All
        (
            request => request.Headers.ContainsKey("If-Match")
        ));
    }

    [TestMethod]
    public async Task WholeFolderMovePreservesMarkersAndDoesNotMoveSimilarlyNamedSiblings()
    {
        await using var server = new WebDavContractServer();

        await ObjectStoreContract.VerifyWholeFolderMoveAsync(server.Store);

        var move = server.Requests.First(request => request.Method == "MOVE");
        Assert.AreEqual("F", move.Headers["Overwrite"]);
        Assert.IsTrue(move.Target.EndsWith('/'));
        Assert.IsTrue(move.Headers["Destination"].EndsWith('/'));
    }

    [TestMethod]
    public async Task WholeFolderMovePreservesNativeEmptyCollections()
    {
        await using var server = new WebDavContractServer();
        await UploadTextAsync(server.Store, "tree/empty/temporary.txt", "temporary");
        Assert.IsTrue(await server.Store.TryDeleteIfContentHashMatchesAsync
        (
            "tree/empty/temporary.txt",
            ArchiveHash.Compute("temporary"u8)
        ));

        await server.Store.MoveFolderAsync("tree", "history/tree");

        Assert.IsFalse(await server.Store.ExistsAsync("tree/empty"));
        Assert.IsTrue(await server.Store.ExistsAsync("history/tree/empty"));
        var children = new List<ArchiveFolderItem>();
        await foreach (var child in server.Store.GetFolderItemsAsync("history/tree")) { children.Add(child); }
        Assert.AreEqual(1, children.Count);
        Assert.IsTrue(children[0].IsFolder);
        Assert.AreEqual("history/tree/empty", children[0].Key);
    }

    [TestMethod]
    public async Task UploadDoesNotOverwriteAnExistingObject()
    {
        await using var server = new WebDavContractServer();

        await ObjectStoreContract.VerifyCreateIfAbsentAsync(server.Store);

        Assert.IsTrue(server.Requests.Where(request => request.Method == "PUT").All
        (
            request => request.Headers["If-None-Match"] == "*"
        ));
    }

    [TestMethod]
    public async Task InterruptedUploadDoesNotPublishPartialBytes()
    {
        await using var server = new WebDavContractServer();

        await ObjectStoreContract.VerifyFailedUploadAsync(server.Store);
    }

    [TestMethod]
    [DataRow("PUT")]
    [DataRow("DELETE")]
    public async Task ConditionalMutationRejectsAChangeAfterTheHashRead(string method)
    {
        await using var server = new WebDavContractServer();
        await UploadTextAsync(server.Store, "document.txt", "original");
        var requestsBeforeMutation = server.Requests.Count;
        server.BeforeRequest = request =>
        {
            if (request.Method == method && request.Headers.ContainsKey("If-Match"))
            {
                server.SetObject("document.txt", "concurrent writer"u8.ToArray());
            }
        };

        var changed = await MutateAsync(server.Store, method, ArchiveHash.Compute("original"u8));

        Assert.IsFalse(changed);
        Assert.AreEqual("concurrent writer", await ReadTextAsync(server.Store, "document.txt"));
        var mutation = server.Requests.Skip(requestsBeforeMutation).Single(request => request.Method == method);
        Assert.IsTrue(mutation.Headers.ContainsKey("If-Match"));
    }

    [TestMethod]
    [DataRow(false, "PUT")]
    [DataRow(true, "PUT")]
    [DataRow(false, "DELETE")]
    [DataRow(true, "DELETE")]
    public async Task ConditionalMutationRequiresAStrongEntityTag(bool weak, string method)
    {
        await using var server = new WebDavContractServer();
        await UploadTextAsync(server.Store, "document.txt", "original");
        var requestsBeforeMutation = server.Requests.Count;
        server.OmitEntityTags = !weak;
        server.UseWeakEntityTags = weak;

        var exception = await Assert.ThrowsExactlyAsync<YabtWebDavException>
        (
            () => MutateAsync(server.Store, method, ArchiveHash.Compute("original"u8))
        );

        StringAssert.Contains(exception.ToString(), "strong ETag");
        Assert.IsFalse(server.Requests.Skip(requestsBeforeMutation).Any(request => request.Method == method));
        Assert.AreEqual("original", await ReadTextAsync(server.Store, "document.txt"));
    }

    [TestMethod]
    [DataRow("PUT")]
    [DataRow("DELETE")]
    public async Task FailedConditionalMutationPropagatesAndPreservesTheOriginal(string method)
    {
        await using var server = new WebDavContractServer();
        await UploadTextAsync(server.Store, "document.txt", "original");
        server.OverrideResponse = request => request.Method == method ? new(503) : null;

        var exception = await Assert.ThrowsExactlyAsync<YabtWebDavException>
        (
            () => MutateAsync(server.Store, method, ArchiveHash.Compute("original"u8))
        );

        StringAssert.Contains(exception.ToString(), "503");
        Assert.AreEqual("original", await ReadTextAsync(server.Store, "document.txt"));
    }

    [TestMethod]
    public async Task MoveRefusesAnOccupiedDestinationAndPreservesBothObjects()
    {
        await using var server = new WebDavContractServer();
        await UploadTextAsync(server.Store, "source.txt", "source");
        await UploadTextAsync(server.Store, "destination.txt", "destination");

        await Assert.ThrowsExactlyAsync<YabtWebDavException>
        (
            () => server.Store.MoveAsync("source.txt", "destination.txt")
        );

        Assert.AreEqual("source", await ReadTextAsync(server.Store, "source.txt"));
        Assert.AreEqual("destination", await ReadTextAsync(server.Store, "destination.txt"));
        Assert.AreEqual("F", server.Requests.Single(request => request.Method == "MOVE").Headers["Overwrite"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UploadRefusesFileAndCollectionCollisionsWithoutReplacingExistingData(bool existingCollection)
    {
        await using var server = new WebDavContractServer();
        var existingKey = existingCollection ? "entry/child.txt" : "entry";
        var desiredKey = existingCollection ? "entry" : "entry/child.txt";
        await UploadTextAsync(server.Store, existingKey, "original");

        await Assert.ThrowsExactlyAsync<YabtWebDavException>
        (
            () => UploadTextAsync(server.Store, desiredKey, "replacement")
        );

        Assert.AreEqual("original", await ReadTextAsync(server.Store, existingKey));
        if (!existingCollection) { Assert.IsFalse(await server.Store.ExistsAsync(desiredKey)); }
    }

    [TestMethod]
    public async Task FailedFolderMovePropagatesWithoutDeletingAnySourceObjects()
    {
        await using var server = new WebDavContractServer();
        await UploadTextAsync(server.Store, "folder/child.txt", "original");
        server.OverrideResponse = request => request.Method == "MOVE" ? new(503) : null;

        await Assert.ThrowsExactlyAsync<YabtWebDavException>
        (
            () => server.Store.MoveFolderAsync("folder", "history/folder")
        );

        Assert.AreEqual("original", await ReadTextAsync(server.Store, "folder/child.txt"));
        Assert.IsFalse(await server.Store.ExistsAsync("history/folder/child.txt"));
        Assert.IsFalse(server.Requests.Any(request => request.Method == "DELETE"));
    }

    [TestMethod]
    public async Task ExistenceFallsBackToDepthZeroWhenHeadIsUnsupported()
    {
        await using var server = new WebDavContractServer();
        await UploadTextAsync(server.Store, "document.txt", "original");
        server.OverrideResponse = request => request.Method == "HEAD" ? new(405) : null;

        Assert.IsTrue(await server.Store.ExistsAsync("document.txt"));
        Assert.IsFalse(await server.Store.ExistsAsync("missing.txt"));

        Assert.AreEqual(2, server.Requests.Count(request => request.Method == "PROPFIND" && request.Headers["Depth"] == "0"));
    }

    [TestMethod]
    public async Task MutationLockContentionAndReacquisitionPreserveOwnership()
    {
        await using var server = new WebDavContractServer();

        await ObjectStoreContract.VerifyLockContentionAndReacquisitionAsync(server.Store);

        Assert.IsFalse(await server.Store.ExistsAsync(ArchiveInternalObjectKeys.MutationLock));
    }

    [TestMethod]
    public async Task MutationLockReleaseDetectsLostOwnershipAndPreservesTheOtherOwner()
    {
        await using var server = new WebDavContractServer();
        var archiveLock = await ((IArchiveMutationLockProvider)server.Store).AcquireArchiveMutationLockAsync();
        var lockLostToken = archiveLock.LockLostToken;
        server.SetObject(ArchiveInternalObjectKeys.MutationLock, "other owner's lock"u8.ToArray());

        await Assert.ThrowsExactlyAsync<YabtCoreException>(() => archiveLock.DisposeAsync().AsTask());

        Assert.IsTrue(lockLostToken.IsCancellationRequested);
        Assert.AreEqual("other owner's lock", await ReadTextAsync(server.Store, ArchiveInternalObjectKeys.MutationLock));
        Assert.IsFalse(server.Requests.Any(request => request.Method == "DELETE"));
    }

    private static async Task<bool> MutateAsync(WebDavObjectStore store, string method, string hash)
    {
        if (method == "DELETE")
        {
            return await store.TryDeleteIfContentHashMatchesAsync("document.txt", hash);
        }

        await using var replacement = new MemoryStream("replacement"u8.ToArray(), writable: false);
        return await store.TryReplaceIfContentHashMatchesAsync("document.txt", hash, replacement, "text/plain", new Dictionary<string, string>());
    }

    private static async Task UploadTextAsync(WebDavObjectStore store, string key, string text)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
        await store.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
    }

    private static async Task<string> ReadTextAsync(WebDavObjectStore store, string key)
    {
        await using var content = await store.OpenReadAsync(key);
        using var reader = new StreamReader(content.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
