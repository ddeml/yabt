using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class YabtAzureBlobServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddYabtAzureBlobObjectStoreBindsDefaultConfigurationSection()
    {
        var configuration = new ConfigurationBuilder().
            AddInMemoryCollection
            (
                new Dictionary<string, string?>
                {
                    [$"{AzureBlobObjectStoreOptions.DefaultConfigurationSectionPath}:ServiceUri"] =
                        "https://default.blob.core.windows.net",
                    [$"{AzureBlobObjectStoreOptions.DefaultConfigurationSectionPath}:ContainerName"] =
                        "history",
                }
            ).
            Build();
        var services = CreateServices(configuration);

        services.AddYabtAzureBlobObjectStore();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<AzureBlobObjectStoreOptions>>().CurrentValue;
        Assert.AreEqual(new Uri("https://default.blob.core.windows.net"), options.ServiceUri);
        Assert.AreEqual("history", options.ContainerName);
    }

    [TestMethod]
    public void AddYabtAzureBlobObjectStoreRegistersDefaultAzureCredentialAndResolver()
    {
        var services = CreateServices(new ConfigurationBuilder().Build());

        services.AddYabtAzureBlobObjectStore();

        using var provider = services.BuildServiceProvider();
        Assert.IsInstanceOfType<DefaultAzureCredential>(provider.GetRequiredService<TokenCredential>());
        var resolvers = provider.GetServices<IBackupRootStoreResolver>();
        var resolver = resolvers.Single
        (
            candidate => candidate.StoreKind == AzureBlobObjectStoreKind.Value
        );
        Assert.AreEqual(AzureBlobObjectStoreKind.Value, resolver.StoreKind);
    }

    [TestMethod]
    public void AddYabtAzureBlobObjectStorePreservesExplicitTokenCredential()
    {
        var credential = new TestTokenCredential();
        var services = CreateServices(new ConfigurationBuilder().Build());
        services.AddSingleton<TokenCredential>(credential);

        services.AddYabtAzureBlobObjectStore();

        using var provider = services.BuildServiceProvider();
        Assert.AreSame(credential, provider.GetRequiredService<TokenCredential>());
    }

    [TestMethod]
    public void AddYabtAzureBlobObjectStoreRejectsBlankConfigurationSectionPath()
    {
        var services = CreateServices(new ConfigurationBuilder().Build());

        var exception = Assert.Throws<ArgumentException>
        (
            () => services.AddYabtAzureBlobObjectStore(" ")
        );

        Assert.AreEqual("configSectionPath", exception.ParamName);
    }

    private static ServiceCollection CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }
}
