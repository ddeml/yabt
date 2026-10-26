using System.Text;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.WebDav.Tests;

[TestClass]
[TestCategory("DeployedService")]
[DoNotParallelize]
public sealed class WebDavDeployedServiceTests
{
    [TestMethod]
    [DataRow("traversal")]
    [DataRow("create-if-absent")]
    [DataRow("conditional-mutations")]
    [DataRow("whole-folder-move")]
    [DataRow("failed-upload")]
    [DataRow("mutation-lock")]
    public async Task ProviderContract(string scenario)
    {
        RequireExplicitOptIn();
        await DeployedServiceFixture.RunAsync
        (
            "webDav",
            scenario,
            fixture => scenario switch
            {
                "traversal" => ObjectStoreContract.VerifyFolderLocalTraversalAsync(fixture.Store),
                "create-if-absent" => ObjectStoreContract.VerifyCreateIfAbsentAsync(fixture.Store),
                "conditional-mutations" => ObjectStoreContract.VerifyConditionalMutationsAsync(fixture.Store),
                "whole-folder-move" => ObjectStoreContract.VerifyWholeFolderMoveAsync(fixture.Store),
                "failed-upload" => ObjectStoreContract.VerifyFailedUploadAsync(fixture.Store),
                "mutation-lock" => ObjectStoreContract.VerifyLockContentionAndReacquisitionAsync(fixture.Store),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            }
        );
    }

    [TestMethod]
    public async Task WholeFolderMovePreservesNativeEmptyCollectionsAndRejectsCollisions()
    {
        RequireExplicitOptIn();
        await DeployedServiceFixture.RunAsync
        (
            "webDav", "native-empty-folder-move", async fixture =>
            {
                await UploadTextAsync(fixture.Store, "tree/empty/temporary.txt", "temporary");
                Assert.IsTrue(await fixture.Store.TryDeleteIfContentHashMatchesAsync
                (
                    "tree/empty/temporary.txt",
                    ArchiveHash.Compute("temporary"u8)
                ));

                await fixture.Store.MoveFolderAsync("tree", "history/tree");

                Assert.IsFalse(await fixture.Store.ExistsAsync("tree/empty"));
                Assert.IsTrue(await fixture.Store.ExistsAsync("history/tree/empty"));
                var children = new List<ArchiveFolderItem>();
                await foreach (var child in fixture.Store.GetFolderItemsAsync("history/tree"))
                {
                    children.Add(child);
                }
                Assert.HasCount(1, children);
                Assert.IsTrue(children[0].IsFolder);
                Assert.AreEqual("history/tree/empty", children[0].Key);

                await UploadTextAsync(fixture.Store, "another/file.txt", "another");
                await Assert.ThrowsExactlyAsync<YabtWebDavException>
                (
                    () => fixture.Store.MoveFolderAsync("another", "history/tree")
                );
                Assert.AreEqual("another", await ReadTextAsync(fixture.Store, "another/file.txt"));
                Assert.IsTrue(await fixture.Store.ExistsAsync("history/tree/empty"));
            }
        );
    }

    private static void RequireExplicitOptIn()
    {
        if (!DeployedServiceFixture.IsEnabled)
        {
            Assert.Inconclusive("Deployed-service acceptance requires explicit opt-in and a private settings path.");
        }
    }

    private static async Task UploadTextAsync(IObjectStore store, string key, string text)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await store.UploadAsync(key, content, "text/plain", new Dictionary<string, string>());
    }

    private static async Task<string> ReadTextAsync(IReadOnlyObjectStore store, string key)
    {
        await using var content = await store.OpenReadAsync(key);
        using var reader = new StreamReader(content.Content, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
