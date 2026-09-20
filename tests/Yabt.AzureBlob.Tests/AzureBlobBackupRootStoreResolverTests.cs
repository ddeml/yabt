using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.AzureBlob.Implementation;
using Yabt.Core.Models;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class AzureBlobBackupRootStoreResolverTests
{
    [TestMethod]
    public void ResolveStoreUsesCustomConfigurationSection()
    {
        const string configSectionPath = "ObjectStores:MainAzure";
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{configSectionPath}:ServiceUri"] = "https://main.blob.core.windows.net",
                [$"{configSectionPath}:ContainerName"] = "history",
                [$"{configSectionPath}:Prefix"] = "personal",
                [$"{configSectionPath}:UploadMaximumConcurrency"] = "3",
                [$"{configSectionPath}:Retry:MaximumRetries"] = "4",
                [$"{configSectionPath}:Retry:Delay"] = "00:00:03",
            }
        );
        var resolver = CreateResolver(configuration);
        var store = new BackupRootStore
        (
            "main",
            AzureBlobObjectStoreKind.Value,
            ConfigSectionPath: configSectionPath
        );

        var context = ResolveContext(resolver, store);

        Assert.AreEqual
        (
            new Uri("https://main.blob.core.windows.net"),
            context.Options.ServiceUri
        );
        Assert.AreEqual("history", context.ContainerClient.Name);
        Assert.AreEqual("personal", context.ObjectStorePrefix);
        Assert.AreEqual(3, context.Options.UploadMaximumConcurrency);
        Assert.AreEqual(3, context.UploadTransferOptions.MaximumConcurrency);
        Assert.AreEqual(4, context.Options.Retry.MaximumRetries);
        Assert.AreEqual(TimeSpan.FromSeconds(3), context.Options.Retry.Delay);
        Assert.AreEqual(4, context.Options.CreateClientOptions().Retry.MaxRetries);
    }

    [TestMethod]
    public void ResolveStoreUsesDefaultConfigurationSection()
    {
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{AzureBlobObjectStoreOptions.DefaultConfigurationSectionPath}:ServiceUri"] =
                    "https://default.blob.core.windows.net",
            }
        );
        var resolver = CreateResolver(configuration);
        var store = new BackupRootStore("default", AzureBlobObjectStoreKind.Value);

        var context = ResolveContext(resolver, store);

        Assert.AreEqual
        (
            new Uri("https://default.blob.core.windows.net/archive"),
            context.ContainerClient.Uri
        );
        Assert.AreEqual(5, context.UploadTransferOptions.MaximumConcurrency);
    }

    [TestMethod]
    public void ResolveStoreBindsConnectionStringWithoutServiceUri()
    {
        const string configSectionPath = "ObjectStores:DevelopmentAzure";
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{configSectionPath}:ConnectionString"] = "UseDevelopmentStorage=true",
                [$"{configSectionPath}:ContainerName"] = "history",
            }
        );
        var resolver = CreateResolver(configuration);
        var store = new BackupRootStore
        (
            "development",
            AzureBlobObjectStoreKind.Value,
            ConfigSectionPath: configSectionPath
        );

        var context = ResolveContext(resolver, store);

        Assert.AreEqual("history", context.ContainerClient.Name);
        Assert.AreEqual("127.0.0.1", context.ContainerClient.Uri.Host);
    }

    [TestMethod]
    public void ResolveStoreDoesNotFallBackWhenCustomSectionIsMissing()
    {
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{AzureBlobObjectStoreOptions.DefaultConfigurationSectionPath}:ServiceUri"] =
                    "https://default.blob.core.windows.net",
            }
        );
        var resolver = CreateResolver(configuration);
        var store = new BackupRootStore
        (
            "missing",
            AzureBlobObjectStoreKind.Value,
            ConfigSectionPath: "ObjectStores:Missing"
        );

        var exception = Assert.Throws<YabtAzureBlobException>
        (
            () => resolver.ResolveStore(store, "descriptor-root")
        );

        StringAssert.Contains(exception.Message, "ObjectStores:Missing");
    }

    [TestMethod]
    public void ResolveStoreRejectsMissingDefaultSection()
    {
        var resolver = CreateResolver(CreateConfiguration([]));
        var store = new BackupRootStore("missing", AzureBlobObjectStoreKind.Value);

        var exception = Assert.Throws<YabtAzureBlobException>
        (
            () => resolver.ResolveStore(store, "descriptor-root")
        );

        StringAssert.Contains
        (
            exception.Message,
            AzureBlobObjectStoreOptions.DefaultConfigurationSectionPath
        );
    }

    [TestMethod]
    public void ResolveStoreRejectsBlankExplicitConfigurationSectionPath()
    {
        var resolver = CreateResolver(CreateConfiguration([]));
        var store = new BackupRootStore
        (
            "blank",
            AzureBlobObjectStoreKind.Value,
            ConfigSectionPath: " "
        );

        var exception = Assert.Throws<YabtAzureBlobException>
        (
            () => resolver.ResolveStore(store, "descriptor-root")
        );

        StringAssert.Contains(exception.Message, "empty configSectionPath");
    }

    [TestMethod]
    public void ResolveStoreRejectsCredentialReference()
    {
        var resolver = CreateResolver(CreateConfiguration([]));
        var store = new BackupRootStore
        (
            "credential-reference",
            AzureBlobObjectStoreKind.Value,
            CredentialRef: string.Empty
        );

        var exception = Assert.Throws<YabtAzureBlobException>
        (
            () => resolver.ResolveStore(store, "descriptor-root")
        );

        StringAssert.Contains(exception.Message, "credentialRef");
    }

    [TestMethod]
    public void ResolveStoreRejectsProviderProperties()
    {
        var resolver = CreateResolver(CreateConfiguration([]));
        var store = new BackupRootStore("legacy", AzureBlobObjectStoreKind.Value)
        {
            ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["accountUri"] = JsonSerializer.SerializeToElement
                (
                    "https://legacy.blob.core.windows.net"
                ),
            },
        };

        var exception = Assert.Throws<YabtAzureBlobException>
        (
            () => resolver.ResolveStore(store, "descriptor-root")
        );

        StringAssert.Contains(exception.Message, "accountUri");
    }

    [TestMethod]
    public void ResolveStoreRejectsDifferentStoreKind()
    {
        var resolver = CreateResolver(CreateConfiguration([]));
        var store = new BackupRootStore("filesystem", "fileSystem");

        var exception = Assert.Throws<YabtAzureBlobException>
        (
            () => resolver.ResolveStore(store, "descriptor-root")
        );

        StringAssert.Contains(exception.Message, "not an Azure Blob object store");
    }

    private static AzureBlobObjectStoreContext ResolveContext
    (
        AzureBlobBackupRootStoreResolver resolver,
        BackupRootStore store
    )
    {
        var objectStore = resolver.ResolveStore(store, "descriptor-root");
        var azureObjectStore = (AzureBlobObjectStore)objectStore;

        return azureObjectStore.GetContext();
    }

    private static AzureBlobBackupRootStoreResolver CreateResolver(IConfiguration configuration) =>
        new
        (
            configuration,
            NullLogger<AzureBlobBackupRootStoreResolver>.Instance,
            NullLogger<AzureBlobObjectStore>.Instance,
            new AzureBlobContainerClientFactory(new TestTokenCredential()),
            TimeProvider.System
        );

    private static IConfiguration CreateConfiguration
    (
        IEnumerable<KeyValuePair<string, string?>> values
    ) => new ConfigurationBuilder().
        AddInMemoryCollection(values).
        Build();
}
