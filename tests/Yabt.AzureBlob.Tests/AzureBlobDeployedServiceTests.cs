using System.Text;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.AzureBlob.Tests;

[TestClass]
[TestCategory("DeployedService")]
[DoNotParallelize]
public sealed class AzureBlobDeployedServiceTests
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
            "azureBlob",
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
    public async Task ExactObjectAndSameNamedPrefixRemainDistinct()
    {
        RequireExplicitOptIn();
        await DeployedServiceFixture.RunAsync
        (
            "azureBlob", "exact-object-and-prefix", async fixture =>
            {
                await UploadTextAsync(fixture.Store, "node", "exact object");
                await UploadTextAsync(fixture.Store, "node/file.txt", "descendant");
                var items = new List<ArchiveFolderItem>();
                await foreach (var item in fixture.Store.GetFolderItemsAsync(null))
                {
                    if (item.Key == "node") { items.Add(item); }
                }

                Assert.HasCount(2, items);
                Assert.HasCount(1, items.Where(item => item.IsObject));
                Assert.HasCount(1, items.Where(item => item.IsFolder));
                await fixture.Store.MoveFolderAsync("node", "history/node");

                Assert.AreEqual("exact object", await ReadTextAsync(fixture.Store, "node"));
                Assert.AreEqual("descendant", await ReadTextAsync(fixture.Store, "history/node/file.txt"));
                Assert.IsFalse(await fixture.Store.ExistsAsync("node/file.txt"));
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
