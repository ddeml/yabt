using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;

namespace Yabt.AzureBlob.Tests;

[TestClass]
public sealed class RuntimeAzureConfigurationWarningsTests
{
    [TestMethod]
    public void CustomSectionWarnsForUnknownNestedKeysWithoutPrintingValues()
    {
        const string sectionPath = "Archives:ColdStorage";
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{sectionPath}:ConnectionString"] = "secret-connection-string",
                [$"{sectionPath}:UploadMaximumConcurrency"] = "2",
                [$"{sectionPath}:Retry:MaximumRetries"] = "3",
                [$"{sectionPath}:Retry:MaximumRetrys"] = "secret-unknown-value",
                [$"{sectionPath}:FutureOption:Password"] = "secret-future-password",
                ["OtherApplication:UnknownSetting"] = "unrelated",
            }
        );
        var logger = new RuntimeAzureWarningLogger();
        using var provider = CreateProvider(configuration, sectionPath, logger);

        var options = provider.GetRequiredService<IOptionsMonitor<AzureBlobObjectStoreOptions>>().CurrentValue;

        Assert.AreEqual("secret-connection-string", options.ConnectionString);
        Assert.AreEqual(2, options.UploadMaximumConcurrency);
        Assert.AreEqual(3, options.Retry.MaximumRetries);
        Assert.HasCount(2, logger.Warnings);
        StringAssert.Contains(logger.Warnings[0], $"{sectionPath}:FutureOption");
        StringAssert.Contains(logger.Warnings[1], $"{sectionPath}:Retry:MaximumRetrys");
        Assert.IsFalse(logger.Warnings.Any(message => message.Contains("secret-", StringComparison.Ordinal)));
        Assert.IsFalse(logger.Warnings.Any(message => message.Contains("OtherApplication", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("Retry", "invalid-object", "Retry")]
    [DataRow("UploadMaximumConcurrency:Nested", "3", "UploadMaximumConcurrency")]
    [DataRow("ServiceUri:Nested", "https://example.test", "ServiceUri")]
    public void MalformedKnownSettingShapesFailInsteadOfWarning
    (
        string setting,
        string value,
        string expectedPath
    )
    {
        const string sectionPath = "Custom:Azure";
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?> { [$"{sectionPath}:{setting}"] = value }
        );
        var logger = new RuntimeAzureWarningLogger();
        using var provider = CreateProvider(configuration, sectionPath, logger);

        var exception = Assert.Throws<YabtException>
        (
            () => _ = provider.GetRequiredService<IOptionsMonitor<AzureBlobObjectStoreOptions>>().CurrentValue
        );

        StringAssert.Contains(exception.Message, $"{sectionPath}:{expectedPath}");
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void InvalidKnownScalarConversionStillFails()
    {
        const string sectionPath = "Custom:Azure";
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{sectionPath}:UploadMaximumConcurrency"] = "not-a-number",
            }
        );
        var logger = new RuntimeAzureWarningLogger();
        using var provider = CreateProvider(configuration, sectionPath, logger);

        var exception = Assert.Throws<InvalidOperationException>
        (
            () => _ = provider.GetRequiredService<IOptionsMonitor<AzureBlobObjectStoreOptions>>().CurrentValue
        );

        StringAssert.Contains(exception.Message, $"{sectionPath}:UploadMaximumConcurrency");
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void ReloadInspectsChangedConfigurationAndPreservesUpdatedOptions()
    {
        const string sectionPath = "Custom:Azure";
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{sectionPath}:UploadMaximumConcurrency"] = "2",
            }
        );
        var logger = new RuntimeAzureWarningLogger();
        using var provider = CreateProvider(configuration, sectionPath, logger);
        var monitor = provider.GetRequiredService<IOptionsMonitor<AzureBlobObjectStoreOptions>>();
        Assert.AreEqual(2, monitor.CurrentValue.UploadMaximumConcurrency);

        configuration[$"{sectionPath}:UploadMaximumConcurrency"] = "4";
        configuration[$"{sectionPath}:UnknownSetting"] = "secret-reloaded-value";
        configuration.Reload();

        Assert.AreEqual(4, monitor.CurrentValue.UploadMaximumConcurrency);
        Assert.HasCount(1, logger.Warnings);
        StringAssert.Contains(logger.Warnings[0], $"{sectionPath}:UnknownSetting");
        Assert.IsFalse(logger.Warnings[0].Contains("secret-reloaded-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeNamesRetainFrameworkCaseInsensitiveBinding()
    {
        const string sectionPath = "Custom:Azure";
        var configuration = CreateConfiguration
        (
            new Dictionary<string, string?>
            {
                [$"{sectionPath}:uploadmaximumconcurrency"] = "2",
                [$"{sectionPath}:retry:maximumretries"] = "3",
            }
        );
        var logger = new RuntimeAzureWarningLogger();
        using var provider = CreateProvider(configuration, sectionPath, logger);

        var options = provider.GetRequiredService<IOptionsMonitor<AzureBlobObjectStoreOptions>>().CurrentValue;

        Assert.AreEqual(2, options.UploadMaximumConcurrency);
        Assert.AreEqual(3, options.Retry.MaximumRetries);
        Assert.IsEmpty(logger.Warnings);
    }

    private static IConfigurationRoot CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider CreateProvider
    (
        IConfiguration configuration,
        string sectionPath,
        RuntimeAzureWarningLogger logger
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<ILogger<AzureBlobObjectStoreOptions>>(logger);
        services.AddYabtAzureBlobObjectStore(sectionPath);
        return services.BuildServiceProvider();
    }

    private sealed class RuntimeAzureWarningLogger : ILogger<AzureBlobObjectStoreOptions>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
