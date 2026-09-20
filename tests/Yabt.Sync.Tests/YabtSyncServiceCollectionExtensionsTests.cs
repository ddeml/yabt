using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class YabtSyncServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddYabtSyncRegistersRestorePathVerifierAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtSync();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IRestorePathVerifier>();
        var second = provider.GetRequiredService<IRestorePathVerifier>();

        Assert.AreSame(first, second);
    }

    [TestMethod]
    public void AddYabtSyncUsesRestoreConcurrencyDefaultOfFive()
    {
        var services = new ServiceCollection();
        services.AddYabtSync();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<YabtSyncOptions>>()
            .CurrentValue;

        Assert.AreEqual(5, options.RestoreMaximumConcurrency);
        Assert.AreEqual(5, options.GetValidatedRestoreMaximumConcurrency());
    }

    [TestMethod]
    public void AddYabtSyncBindsConfiguredRestoreConcurrency()
    {
        const string configSectionPath = "Custom:Sync";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{configSectionPath}:RestoreMaximumConcurrency"] = "3",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddYabtSync(configSectionPath);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<YabtSyncOptions>>()
            .CurrentValue;

        Assert.AreEqual(3, options.GetValidatedRestoreMaximumConcurrency());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void RestoreMaximumConcurrencyRejectsNonPositiveValues(int maximumConcurrency)
    {
        var options = new YabtSyncOptions
        {
            RestoreMaximumConcurrency = maximumConcurrency,
        };

        var exception = Assert.Throws<YabtSyncException>(
            () => options.GetValidatedRestoreMaximumConcurrency());

        StringAssert.Contains(exception.Message, "greater than zero");
    }

    [TestMethod]
    public void AddYabtSyncRejectsEmptyConfigurationSectionPath()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddYabtSync(" "));

        Assert.AreEqual("configSectionPath", exception.ParamName);
    }
}
