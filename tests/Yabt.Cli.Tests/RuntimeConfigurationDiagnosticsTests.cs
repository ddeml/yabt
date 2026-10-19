using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Sync;
using Yabt.WebDav;

namespace Yabt.Cli.Tests;

[TestClass]
public sealed class RuntimeConfigurationDiagnosticsTests
{
    [TestMethod]
    [DataRow("CredentialRef", "unused-credential-reference", 2)]
    [DataRow("credentialref", "unused-credential-reference", 2)]
    [DataRow("CredentialRef", "", 1)]
    public void WebDavWarnsForUnsupportedCredentialReferenceAndUnknownSettings
    (
        string credentialSettingName,
        string credentialReference,
        int expectedWarnings
    )
    {
        const string sectionPath = "Custom:WebDav";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                [$"{sectionPath}:Endpoint"] = "https://example.test/archive/",
                [$"{sectionPath}:{credentialSettingName}"] = credentialReference,
                [$"{sectionPath}:Password"] = "secret-password",
                [$"{sectionPath}:FutureSetting"] = "secret-ignored-value",
            }
        ).Build();
        var logger = new RuntimeDiagnosticLogger<WebDavObjectStoreOptions>();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILogger<WebDavObjectStoreOptions>>(logger);
        services.AddYabtWebDavObjectStore(sectionPath);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<WebDavObjectStoreOptions>>().CurrentValue;

        Assert.AreEqual(new Uri("https://example.test/archive/"), options.Endpoint);
        Assert.AreEqual("secret-password", options.Password);
        Assert.HasCount(expectedWarnings, logger.Warnings);
        Assert.IsTrue(logger.Warnings.Any(message => message.Contains($"{sectionPath}:FutureSetting", StringComparison.Ordinal)));
        Assert.IsFalse(logger.Warnings.Any(message => message.Contains("secret-", StringComparison.Ordinal)));
        if (credentialReference.Length > 0)
        {
            Assert.IsTrue(logger.Warnings.Any(message => message.Contains($"{sectionPath}:{credentialSettingName}", StringComparison.Ordinal)));
            Assert.IsTrue(logger.Warnings.Any(message => message.Contains("anonymous", StringComparison.Ordinal)));
            Assert.IsFalse(logger.Warnings.Any(message => message.Contains(credentialReference, StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void DirectInspectionMatchesUnsupportedSettingsIndependentlyOfDictionaryComparer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["WebDav:credentialref"] = "secret-reference",
            }
        ).Build();
        var logger = new RuntimeDiagnosticLogger<WebDavObjectStoreOptions>();

        RuntimeConfigurationWarnings.Inspect<WebDavObjectStoreOptions>
        (
            configuration,
            "WebDav",
            logger,
            new Dictionary<string, string>
            {
                [nameof(WebDavObjectStoreOptions.CredentialRef)] = "Credential references are unsupported.",
            }
        );

        Assert.HasCount(1, logger.Warnings);
        StringAssert.Contains(logger.Warnings[0], "WebDav:credentialref");
        StringAssert.Contains(logger.Warnings[0], "Credential references are unsupported.");
        Assert.IsFalse(logger.Warnings[0].Contains("secret-reference", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnknownSettingWarningIdentifiesWinningJsonSourceFile()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"yabt-config-first-{Guid.NewGuid():N}.json");
        var secondPath = Path.Combine(Path.GetTempPath(), $"yabt-config-second-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(firstPath, """{"Sync":{"UnknownSetting":"first-secret"}}""");
            File.WriteAllText(secondPath, """{"Sync":{"UnknownSetting":"second-secret"}}""");
            var configuration = new ConfigurationBuilder().
                AddJsonFile(firstPath).
                AddJsonFile(secondPath).
                Build();
            using var configurationLifetime = (IDisposable)configuration;
            var logger = new RuntimeDiagnosticLogger<YabtSyncOptions>();

            RuntimeConfigurationWarnings.Inspect<YabtSyncOptions>(configuration, "Sync", logger);

            Assert.HasCount(1, logger.Warnings);
            StringAssert.Contains(logger.Warnings[0], Path.GetFileName(secondPath));
            StringAssert.Contains(logger.Warnings[0], "Sync:UnknownSetting");
            Assert.IsFalse(logger.Warnings[0].Contains(Path.GetFileName(firstPath), StringComparison.Ordinal));
            Assert.IsFalse(logger.Warnings[0].Contains("secret", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    private sealed class RuntimeDiagnosticLogger<TOptions> : ILogger<TOptions>
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
