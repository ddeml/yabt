using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.FileSystem;
using Yabt.Format.Zip;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RuntimeProviderConfigurationWarningsTests
{
    [TestMethod]
    public void UnknownSettingsDoNotRequireHostLoggingServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["Custom:Sync:RestoreMaximumConcurrency"] = "2",
                ["Custom:Sync:UnknownSetting"] = "ignored",
            }
        ).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddYabtSync("Custom:Sync");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<YabtSyncOptions>>().CurrentValue;

        Assert.AreEqual(2, options.RestoreMaximumConcurrency);
    }

    [TestMethod]
    public void FileSystemZipAndSyncInspectOnlyTheirRegisteredSections()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["Custom:Files:BufferSize"] = "1024",
                ["Custom:Files:FutureSetting"] = "ignored-file-value",
                ["Custom:Zip:CompressionLevel"] = "SmallestSize",
                ["Custom:Zip:FutureSetting"] = "ignored-zip-value",
                ["Custom:Sync:RestoreMaximumConcurrency"] = "2",
                ["Custom:Sync:FutureSetting"] = "ignored-sync-value",
                ["Logging:ThirdPartyLoggingOption"] = "unrelated",
            }
        ).Build();
        var warnings = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILogger<FileSystemObjectStoreOptions>>
        (
            new RuntimeProviderWarningLogger<FileSystemObjectStoreOptions>(warnings)
        );
        services.AddSingleton<ILogger<ZipArchiveFormatOptions>>
        (
            new RuntimeProviderWarningLogger<ZipArchiveFormatOptions>(warnings)
        );
        services.AddSingleton<ILogger<YabtSyncOptions>>
        (
            new RuntimeProviderWarningLogger<YabtSyncOptions>(warnings)
        );
        services.AddYabtFileSystemObjectStore("Custom:Files");
        services.AddYabtZipFormatHandler("Custom:Zip");
        services.AddYabtSync("Custom:Sync");
        using var provider = services.BuildServiceProvider();

        var fileSystemOptions = provider.GetRequiredService<IOptionsMonitor<FileSystemObjectStoreOptions>>().CurrentValue;
        var zipOptions = provider.GetRequiredService<IOptionsMonitor<ZipArchiveFormatOptions>>().CurrentValue;
        var syncOptions = provider.GetRequiredService<IOptionsMonitor<YabtSyncOptions>>().CurrentValue;
        Assert.AreEqual(1024, fileSystemOptions.BufferSize);
        Assert.AreEqual(System.IO.Compression.CompressionLevel.SmallestSize, zipOptions.CompressionLevel);
        Assert.AreEqual(2, syncOptions.RestoreMaximumConcurrency);
        Assert.HasCount(3, warnings);
        Assert.IsTrue(warnings.Any(message => message.Contains("Custom:Files:FutureSetting", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(message => message.Contains("Custom:Zip:FutureSetting", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(message => message.Contains("Custom:Sync:FutureSetting", StringComparison.Ordinal)));
        Assert.IsFalse(warnings.Any(message => message.Contains("ignored-", StringComparison.Ordinal)));
        Assert.IsFalse(warnings.Any(message => message.Contains("Logging", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("unknown-level")]
    [DataRow("99")]
    public void UnsupportedCompressionLevelFailsWithoutTreatingItAsOptional(string compressionLevel)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["Custom:Zip:CompressionLevel"] = compressionLevel,
            }
        ).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddYabtZipFormatHandler("Custom:Zip");
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<YabtException>
        (
            () => _ = provider.GetRequiredService<IOptionsMonitor<ZipArchiveFormatOptions>>().CurrentValue
        );

        StringAssert.Contains(exception.Message, "Custom:Zip:CompressionLevel");
    }

    private sealed class RuntimeProviderWarningLogger<TOptions>(List<string> _warnings) : ILogger<TOptions>
    {
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
            if (logLevel == LogLevel.Warning) _warnings.Add(formatter(state, exception));
        }
    }
}
