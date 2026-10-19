using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Models;
using Yabt.FileSystem.Implementation;

namespace Yabt.FileSystem.Tests;

[TestClass]
public sealed class FileSystemRootConfigurationTests
{
    [TestMethod]
    public void ResolveStoreWarnsAboutUnknownPropertiesAndUnusedCredentialsWithoutLoggingValues()
    {
        const string privateValue = "must-not-be-logged";
        var logger = new CapturingFileSystemConfigurationLogger();
        var resolver = new FileSystemBackupRootStoreResolver(logger);
        var store = CreateStore(new Dictionary<string, JsonElement>
        {
            ["rootPath"] = JsonSerializer.SerializeToElement("archive"),
            ["listChunks"] = JsonSerializer.SerializeToElement(privateValue),
        }) with { CredentialRef = privateValue };

        _ = resolver.ResolveStore(store, Path.GetTempPath());

        Assert.HasCount(2, logger.Warnings);
        Assert.IsTrue(logger.Warnings.All(warning => warning.Contains(".yabt-root.json", StringComparison.Ordinal)));
        Assert.IsTrue(logger.Warnings.Any(warning => warning.Contains(".credentialRef", StringComparison.Ordinal)));
        Assert.IsTrue(logger.Warnings.Any(warning => warning.Contains("[\"listChunks\"]", StringComparison.Ordinal)));
        Assert.IsFalse(logger.Warnings.Any(warning => warning.Contains(privateValue, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ResolveStoreAcceptsSupportedPropertiesWithoutWarnings()
    {
        var logger = new CapturingFileSystemConfigurationLogger();
        var resolver = new FileSystemBackupRootStoreResolver(logger);
        var store = CreateStore(new Dictionary<string, JsonElement>
        {
            ["rootPath"] = JsonSerializer.SerializeToElement("archive"),
            ["bufferSize"] = JsonSerializer.SerializeToElement(4096),
            ["listChunkSize"] = JsonSerializer.SerializeToElement(10),
        });

        _ = resolver.ResolveStore(store, Path.GetTempPath());

        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void ResolveStoreWarnsAndIgnoresUnsupportedConfigSectionPath()
    {
        const string ignoredSection = "ObjectStores:UnusedFileSystem";
        var logger = new CapturingFileSystemConfigurationLogger();
        var resolver = new FileSystemBackupRootStoreResolver(logger);
        var store = CreateStore(new Dictionary<string, JsonElement>
        {
            ["rootPath"] = JsonSerializer.SerializeToElement("archive"),
        }) with { ConfigSectionPath = ignoredSection };

        var resolvedStore = (FileSystemObjectStore)resolver.ResolveStore(store, Path.GetTempPath());

        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "archive"), resolvedStore.RootPath);
        Assert.HasCount(1, logger.Warnings);
        StringAssert.Contains(logger.Warnings[0], ".configSectionPath");
        StringAssert.Contains(logger.Warnings[0], "ignored");
        Assert.IsFalse(logger.Warnings[0].Contains(ignoredSection, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolveStoreDeduplicatesWarningsWithinAnOperation()
    {
        var logger = new CapturingFileSystemConfigurationLogger();
        var resolver = new FileSystemBackupRootStoreResolver(logger);
        var store = CreateStore(new Dictionary<string, JsonElement>
        {
            ["rootPath"] = JsonSerializer.SerializeToElement("archive"),
            ["futureSetting"] = JsonSerializer.SerializeToElement(true),
        });
        using var operation = ConfigurationWarnings.BeginOperation();

        _ = resolver.ResolveStore(store, Path.GetTempPath());
        _ = resolver.ResolveStore(store, Path.GetTempPath());

        Assert.HasCount(1, logger.Warnings);
    }

    [DataRow("rootPath", "42")]
    [DataRow("rootPath", "{}")]
    [DataRow("bufferSize", "\"4096\"")]
    [DataRow("bufferSize", "0")]
    [DataRow("listChunkSize", "-1")]
    [TestMethod]
    public void ResolveStoreRejectsInvalidKnownProperties(string propertyName, string json)
    {
        var logger = new CapturingFileSystemConfigurationLogger();
        var resolver = new FileSystemBackupRootStoreResolver(logger);
        var properties = new Dictionary<string, JsonElement>
        {
            ["rootPath"] = JsonSerializer.SerializeToElement("archive"),
        };
        properties[propertyName] = JsonSerializer.Deserialize<JsonElement>(json);
        var store = CreateStore(properties);

        var error = Assert.ThrowsExactly<YabtFileSystemException>
        (
            () => resolver.ResolveStore(store, Path.GetTempPath())
        );

        StringAssert.Contains(error.Message, propertyName);
    }

    private static BackupRootStore CreateStore(Dictionary<string, JsonElement> properties) =>
        new("archive", FileSystemObjectStoreKind.Value) { ProviderProperties = properties };

    private sealed class CapturingFileSystemConfigurationLogger : ILogger<FileSystemObjectStore>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning) { Warnings.Add(formatter(state, exception)); }
        }
    }
}
