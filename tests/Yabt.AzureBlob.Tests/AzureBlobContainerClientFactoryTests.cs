using Yabt.AzureBlob.Implementation;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class AzureBlobContainerClientFactoryTests
{
    [TestMethod]
    public void CreateUsesConnectionStringBeforeServiceUri()
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ConnectionString = "UseDevelopmentStorage=true",
            ServiceUri = new Uri("https://ignored.blob.core.windows.net"),
            ContainerName = "history",
        };

        var client = factory.Create(options);

        Assert.AreEqual("history", client.Name);
        Assert.AreEqual("127.0.0.1", client.Uri.Host);
    }

    [TestMethod]
    public void CreateUsesConnectionStringWithoutServiceUri()
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ConnectionString = "UseDevelopmentStorage=true",
            ContainerName = "history",
        };

        var client = factory.Create(options);

        Assert.AreEqual("history", client.Name);
        Assert.AreEqual("127.0.0.1", client.Uri.Host);
    }

    [TestMethod]
    public void CreateUsesTokenCredentialWithServiceUri()
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("https://example.blob.core.windows.net"),
            ContainerName = "history",
        };

        var client = factory.Create(options);

        Assert.AreEqual(new Uri("https://example.blob.core.windows.net/history"), client.Uri);
    }

    [TestMethod]
    public void CreateTreatsWhitespaceConnectionStringAsMissing()
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ConnectionString = " ",
            ServiceUri = new Uri("https://example.blob.core.windows.net"),
            ContainerName = "history",
        };

        var client = factory.Create(options);

        Assert.AreEqual(new Uri("https://example.blob.core.windows.net/history"), client.Uri);
    }

    [TestMethod]
    public void CreateDefaultsContainerNameToArchive()
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("https://example.blob.core.windows.net"),
        };

        var client = factory.Create(options);

        Assert.AreEqual("archive", client.Name);
    }

    [TestMethod]
    public void CreateRejectsMissingServiceUriWithoutConnectionString()
    {
        var factory = CreateFactory();

        var exception = Assert.Throws<YabtAzureBlobException>
        (
            () => factory.Create(new AzureBlobObjectStoreOptions())
        );

        StringAssert.Contains(exception.Message, "cannot determine the storage endpoint");
    }

    [TestMethod]
    public void CreateRejectsRelativeServiceUri()
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("relative", UriKind.Relative),
        };

        var exception = Assert.Throws<YabtAzureBlobException>(() => factory.Create(options));

        StringAssert.Contains(exception.Message, "absolute service URI");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("UseDevelopmentStorage=true")]
    public void CreateRejectsNonHttpsServiceUri(string? connectionString)
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ConnectionString = connectionString,
            ServiceUri = new Uri("http://example.blob.core.windows.net"),
        };

        var exception = Assert.Throws<YabtAzureBlobException>(() => factory.Create(options));

        StringAssert.Contains(exception.Message, "must use HTTPS");
    }

    [TestMethod]
    [DataRow("https://user@example.blob.core.windows.net")]
    [DataRow("https://example.blob.core.windows.net?sig=secret")]
    [DataRow("https://example.blob.core.windows.net#fragment")]
    public void CreateRejectsUnsafeServiceUriEvenWhenConnectionStringExists(string serviceUri)
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ConnectionString = "UseDevelopmentStorage=true",
            ServiceUri = new Uri(serviceUri),
        };

        var exception = Assert.Throws<YabtAzureBlobException>(() => factory.Create(options));

        StringAssert.Contains(exception.Message, "must not contain");
    }

    [TestMethod]
    public void CreateRejectsWhitespaceContainerName()
    {
        var factory = CreateFactory();
        var options = new AzureBlobObjectStoreOptions
        {
            ServiceUri = new Uri("https://example.blob.core.windows.net"),
            ContainerName = " ",
        };

        var exception = Assert.Throws<YabtAzureBlobException>(() => factory.Create(options));

        StringAssert.Contains(exception.Message, "container name");
    }

    private static AzureBlobContainerClientFactory CreateFactory() =>
        new(new TestTokenCredential());
}
