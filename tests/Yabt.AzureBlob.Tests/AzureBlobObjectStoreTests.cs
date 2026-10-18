using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.AzureBlob.Implementation;
using Yabt.Tests;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class AzureBlobObjectStoreTests
{
    [TestMethod]
    public async Task EnsureReadyAsyncReadsExistingContainerWithoutCreatingIt()
    {
        await using var server = new TestHttpServer(200);
        var store = CreateHttpStore(server.Endpoint);

        await store.EnsureReadyAsync();

        var request = server.Requests.Single();
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual("/archive?restype=container", request.Target);
    }

    [TestMethod]
    [DataRow(404)]
    [DataRow(403)]
    [DataRow(503)]
    public async Task EnsureReadyAsyncRejectsMissingOrUnavailableContainerWithoutCreatingIt(int statusCode)
    {
        await using var server = new TestHttpServer(statusCode);
        var store = CreateHttpStore(server.Endpoint);

        var exception = await Assert.ThrowsExactlyAsync<YabtAzureBlobException>
        (
            () => store.EnsureReadyAsync()
        );

        StringAssert.Contains(exception.Message, "existing configured container");
        Assert.AreEqual("GET", server.Requests.Single().Method);
    }

    [TestMethod]
    public async Task InitializeAsyncExplicitlyCreatesTheContainer()
    {
        await using var server = new TestHttpServer(201);
        var store = CreateHttpStore(server.Endpoint);

        await store.InitializeAsync();

        var request = server.Requests.Single();
        Assert.AreEqual("PUT", request.Method);
        Assert.AreEqual("/archive?restype=container", request.Target);
    }

    [TestMethod]
    public void BlobPathsPreserveWhitespaceIncludingWhitespaceOnlySegments()
    {
        Assert.AreEqual(" / file ", AzureBlobObjectStore.CombineBlobNameParts(" ", " file "));
        Assert.AreEqual("folder/ ", AzureBlobObjectStore.CombineBlobNameParts(null, "folder", " "));
        Assert.AreEqual(" file ", AzureBlobObjectStore.ToObjectStoreKey(" ", " / file "));
        Assert.AreEqual(" file", AzureBlobObjectStore.ToObjectStoreKey("", " file"));
        Assert.AreEqual("file", AzureBlobObjectStore.ToObjectStoreKey("", "file"));
    }

    [TestMethod]
    [DataRow("folder\\literal.txt")]
    [DataRow("folder//literal.txt")]
    public void DiscoveredBlobNamesCannotBeReinterpretedAsDifferentPaths(string blobName)
    {
        var exception = Assert.ThrowsExactly<YabtAzureBlobException>
        (
            () => AzureBlobObjectStore.ToObjectStoreKey("", blobName)
        );

        StringAssert.Contains(exception.Message, blobName);
        Assert.IsInstanceOfType<ArgumentException>(exception.InnerException);
    }

    [TestMethod]
    public void ClientRetryOptionsUseResilientDefaults()
    {
        var options = new AzureBlobObjectStoreOptions();

        var clientOptions = options.CreateClientOptions();

        Assert.AreEqual(RetryMode.Exponential, clientOptions.Retry.Mode);
        Assert.AreEqual(8, clientOptions.Retry.MaxRetries);
        Assert.AreEqual(TimeSpan.FromSeconds(2), clientOptions.Retry.Delay);
        Assert.AreEqual(TimeSpan.FromSeconds(30), clientOptions.Retry.MaxDelay);
        Assert.AreEqual(TimeSpan.FromSeconds(100), clientOptions.Retry.NetworkTimeout);
    }

    [TestMethod]
    public void CreateClientOptionsUsesConfiguredRetryPolicy()
    {
        var options = new AzureBlobObjectStoreOptions
        {
            Retry = new AzureBlobRetryOptions
            {
                MaximumRetries = 2,
                Delay = TimeSpan.FromSeconds(3),
                MaximumDelay = TimeSpan.FromSeconds(20),
                NetworkTimeout = TimeSpan.FromSeconds(45),
            },
        };

        var clientOptions = options.CreateClientOptions();

        Assert.AreEqual(2, clientOptions.Retry.MaxRetries);
        Assert.AreEqual(TimeSpan.FromSeconds(3), clientOptions.Retry.Delay);
        Assert.AreEqual(TimeSpan.FromSeconds(20), clientOptions.Retry.MaxDelay);
        Assert.AreEqual(TimeSpan.FromSeconds(45), clientOptions.Retry.NetworkTimeout);
    }

    [TestMethod]
    public void CreateClientOptionsRejectsInvalidRetryPolicy()
    {
        var options = new AzureBlobObjectStoreOptions
        {
            Retry = new AzureBlobRetryOptions
            {
                MaximumRetries = -1,
            },
        };

        var exception = Assert.Throws<YabtAzureBlobException>(
            () => options.CreateClientOptions());

        StringAssert.Contains(exception.Message, "must not be negative");
    }

    [TestMethod]
    public void UploadMaximumConcurrencyDefaultsToFive()
    {
        var options = new AzureBlobObjectStoreOptions();

        var transferOptions = options.CreateUploadTransferOptions();

        Assert.AreEqual(5, options.UploadMaximumConcurrency);
        Assert.AreEqual(5, transferOptions.MaximumConcurrency);
    }

    [TestMethod]
    public void CreateUploadTransferOptionsUsesConfiguredMaximumConcurrency()
    {
        var options = new AzureBlobObjectStoreOptions
        {
            UploadMaximumConcurrency = 2,
        };

        var transferOptions = options.CreateUploadTransferOptions();

        Assert.AreEqual(2, transferOptions.MaximumConcurrency);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void CreateUploadTransferOptionsRejectsNonPositiveMaximumConcurrency
    (
        int maximumConcurrency
    )
    {
        var options = new AzureBlobObjectStoreOptions
        {
            UploadMaximumConcurrency = maximumConcurrency,
        };

        var exception = Assert.Throws<YabtAzureBlobException>(
            () => options.CreateUploadTransferOptions());

        StringAssert.Contains(exception.Message, "greater than zero");
    }

    [TestMethod]
    public void GetContextReusesClientUntilOptionsChange()
    {
        var firstOptions = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("https://first.blob.core.windows.net"),
            ContainerName = "first",
            Prefix = "one",
            UploadMaximumConcurrency = 1,
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
        Assert.AreEqual(1, firstContext.UploadTransferOptions.MaximumConcurrency);

        var secondOptions = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("https://second.blob.core.windows.net"),
            ContainerName = "second",
            Prefix = "two",
            UploadMaximumConcurrency = 2,
        };
        options.Set(secondOptions);

        var secondContext = store.GetContext();

        Assert.AreNotSame(firstContext, secondContext);
        Assert.AreSame(secondOptions, secondContext.Options);
        Assert.AreEqual(new Uri("https://second.blob.core.windows.net/second"), secondContext.ContainerClient.Uri);
        Assert.AreEqual("two", secondContext.ObjectStorePrefix);
        Assert.AreEqual(2, secondContext.UploadTransferOptions.MaximumConcurrency);
    }
    private static AzureBlobObjectStore CreateHttpStore(Uri endpoint)
    {
        var options = new AzureBlobObjectStoreOptions
        {
            ConnectionString = "DefaultEndpointsProtocol=http;AccountName=testaccount;" +
                $"AccountKey={Convert.ToBase64String(new byte[32])};BlobEndpoint={endpoint}",
            ContainerName = "archive",
            Retry = new AzureBlobRetryOptions { MaximumRetries = 0 },
        };
        return new
        (
            new TestOptionsMonitor<AzureBlobObjectStoreOptions>(options),
            new AzureBlobContainerClientFactory(new TestTokenCredential()),
            NullLogger<AzureBlobObjectStore>.Instance,
            TimeProvider.System
        );
    }
}
