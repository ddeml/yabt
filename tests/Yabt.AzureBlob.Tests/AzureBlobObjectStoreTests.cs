using Microsoft.Extensions.Logging.Abstractions;
using Yabt.AzureBlob.Implementation;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class AzureBlobObjectStoreTests
{
    [TestMethod]
    public void GetContextReusesClientUntilOptionsChange()
    {
        var firstOptions = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("https://first.blob.core.windows.net"),
            ContainerName = "first",
            Prefix = "one",
        };
        var options = new TestOptionsMonitor<AzureBlobObjectStoreOptions>(firstOptions);
        var store = new AzureBlobObjectStore
        (
            options,
            new AzureBlobContainerClientFactory(new TestTokenCredential()),
            NullLogger<AzureBlobObjectStore>.Instance,
            TimeProvider.System
        );

        var firstContext = store.GetContext();
        var repeatedContext = store.GetContext();

        Assert.AreSame(firstContext, repeatedContext);

        var secondOptions = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("https://second.blob.core.windows.net"),
            ContainerName = "second",
            Prefix = "two",
        };
        options.Set(secondOptions);

        var secondContext = store.GetContext();

        Assert.AreNotSame(firstContext, secondContext);
        Assert.AreSame(secondOptions, secondContext.Options);
        Assert.AreEqual(new Uri("https://second.blob.core.windows.net/second"), secondContext.ContainerClient.Uri);
        Assert.AreEqual("two", secondContext.ObjectStorePrefix);
    }
}
