using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.AzureBlob.Implementation;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class AzureBlobObjectStoreTests
{
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
}
