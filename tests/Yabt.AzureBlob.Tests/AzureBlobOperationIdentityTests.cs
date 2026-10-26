using Microsoft.Extensions.Logging.Abstractions;
using Yabt.AzureBlob.Implementation;
using Yabt.Core.Abstractions;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class AzureBlobOperationIdentityTests
{
    [TestMethod]
    public void IdentityDistinguishesEffectiveEndpointContainerAndExactPrefix()
    {
        var original = Identity("https://first.blob.core.windows.net", "archive", "prefix");

        Assert.AreEqual(original, Identity("https://first.blob.core.windows.net", "archive", "prefix"));
        Assert.AreNotEqual(original, Identity("https://second.blob.core.windows.net", "archive", "prefix"));
        Assert.AreNotEqual(original, Identity("https://first.blob.core.windows.net", "other", "prefix"));
        Assert.AreNotEqual(original, Identity("https://first.blob.core.windows.net", "archive", "Prefix"));
        Assert.AreNotEqual(original, Identity("https://first.blob.core.windows.net", "archive", "prefix "));
        Assert.IsFalse(original.Contains("first", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IdentityUsesConnectionStringEndpointAndExcludesCredentials()
    {
        const string endpoint = "https://actual.blob.core.windows.net";
        var tokenIdentity = Identity(endpoint, "archive", "prefix");
        foreach (var signature in new[] { "secret-one", "secret-two" })
        {
            var store = CreateStore(new(new()
            {
                ConnectionString = $"BlobEndpoint={endpoint};SharedAccessSignature=sv=2025-01-05&sig={signature}",
                ServiceUri = new("https://ignored.blob.core.windows.net"),
                ContainerName = "archive",
                Prefix = "prefix",
            }));

            Assert.AreEqual(tokenIdentity, store.OperationIdentity);
            Assert.IsFalse(store.OperationIdentity.Contains(signature, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void IdentityObservesReloadedLocation()
    {
        var options = new TestOptionsMonitor<AzureBlobObjectStoreOptions>(new()
        {
            ServiceUri = new("https://first.blob.core.windows.net"),
        });
        var store = CreateStore(options);
        var original = ((IObjectStoreOperationIdentity)store).OperationIdentity;

        options.Set(new() { ServiceUri = new("https://second.blob.core.windows.net") });

        Assert.AreNotEqual(original, store.OperationIdentity);
    }

    private static string Identity(string endpoint, string container, string prefix) => CreateStore(new(new()
    {
        ServiceUri = new(endpoint),
        ContainerName = container,
        Prefix = prefix,
    })).OperationIdentity;

    private static AzureBlobObjectStore CreateStore(TestOptionsMonitor<AzureBlobObjectStoreOptions> options) => new
    (
        options,
        new AzureBlobContainerClientFactory(new TestTokenCredential()),
        NullLogger<AzureBlobObjectStore>.Instance,
        TimeProvider.System
    );
}
