using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Common;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Tests;

namespace Yabt.Format.Mirror.Tests;

[TestClass]
public sealed class MirrorArchiveFormatHandlerTests
{
    [TestMethod]
    public async Task ProjectBackupAsyncWarnsOnceForIgnoredSettingsAndStillArchivesMatchingFiles()
    {
        var warnings = new List<string>();
        var services = CreateServices();
        services.AddSingleton(warnings);
        services.AddSingleton(typeof(ILogger<>), typeof(MirrorPolicyCaptureLogger<>));
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IArchiveFormatHandler>();
        var source = new MemoryObjectStore();
        await UploadTextAsync(source, "photos/private.secret", "private content");
        var policy = new FolderPolicy
        (
            MirrorArchiveFormatName.Value,
            IncludePatterns: ["*.txt"],
            ExcludePatterns: ["*.secret"],
            Options: new { SecretSetting = "never-log-this-value" }
        );
        const string configurationSource = "source/photos/.yabt-policy.json";
        FolderPolicyDiagnostics.SetConfigurationSource(policy, configurationSource);

        using var operation = ConfigurationWarnings.BeginOperation();
        handler.ValidateBackupPolicy(policy);
        var objects = await CollectProjectedObjectsAsync
        (
            handler.ProjectBackupAsync(new(source, "photos", policy))
        );

        Assert.HasCount(3, warnings);
        Assert.IsTrue(warnings.All(message =>
            message.Contains(configurationSource, StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(message =>
            message.Contains("Files matching these patterns may be archived.", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.All(message =>
            !message.Contains("never-log-this-value", StringComparison.Ordinal)));
        Assert.AreEqual("private.secret", objects.Single().RelativePath);
        await AssertProjectedTextAsync(objects.Single(), "private content");

        await using var restored = await handler.ProjectRestoreAsync(new(objects.Single()));
        Assert.HasCount(3, warnings);
        await AssertProjectedTextAsync(restored.Objects.Single(), "private content");
    }

    [TestMethod]
    public void ValidateBackupPolicyDoesNotWarnForEmptyOptionalSettings()
    {
        var warnings = new List<string>();
        var services = CreateServices();
        services.AddSingleton(warnings);
        services.AddSingleton(typeof(ILogger<>), typeof(MirrorPolicyCaptureLogger<>));
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IArchiveFormatHandler>();

        handler.ValidateBackupPolicy(new(IncludePatterns: [], ExcludePatterns: [], Options: new { }));
        handler.ValidateBackupPolicy(FolderPolicy.Default);

        Assert.IsEmpty(warnings);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncRejectsInvalidProgrammaticOptions()
    {
        using var provider = CreateServices().BuildServiceProvider();
        var handler = provider.GetRequiredService<IArchiveFormatHandler>();

        await Assert.ThrowsExactlyAsync<YabtCoreException>
        (
            () => CollectProjectedObjectsAsync
            (
                handler.ProjectBackupAsync
                (
                    new(new MemoryObjectStore(), Policy: new(Options: "invalid"))
                )
            )
        );
    }

    [TestMethod]
    public void ServiceRegistrationRegistersMirrorFormatHandler()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();

        var handlers = serviceProvider.GetServices<IArchiveFormatHandler>().ToArray();

        Assert.AreEqual(1, handlers.Length);
        var handler = handlers[0];
        Assert.AreEqual(MirrorArchiveFormatName.Value, handler.FormatName);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncMapsSourceObjectsOneToOne()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var sourceStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(MirrorArchiveFormatName.Value)
        )));

        var projectedObject = projectedObjects.Single();
        Assert.AreEqual("folder/file.txt", projectedObject.RelativePath);
        StringAssert.StartsWith(
            projectedObject.ChangeFingerprint,
            "stat-v1:");
        await AssertProjectedTextAsync(projectedObject, "source content");
    }

    [TestMethod]
    public void ArchiveChangeFingerprintUsesCanonicalUtcTimeAndCompleteMetadata()
    {
        var localTime = new DateTimeOffset(
            2026,
            8,
            16,
            14,
            30,
            0,
            TimeSpan.FromHours(2));

        var localFingerprint = ArchiveChangeFingerprint.Create(42, localTime);
        var utcFingerprint = ArchiveChangeFingerprint.Create(42, localTime.ToUniversalTime());
        var differentLengthFingerprint = ArchiveChangeFingerprint.Create(43, localTime);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArchiveChangeFingerprint.Create(-1, localTime));
        var hasIncompleteFingerprint = ArchiveChangeFingerprint.TryCreate(
            42,
            lastModifiedUtc: null,
            out var incompleteFingerprint);
        var parsed = ArchiveChangeFingerprint.TryParse(
            localFingerprint,
            out var parsedLength,
            out var parsedLastModifiedUtc);
        var malformedParsed = ArchiveChangeFingerprint.TryParse(
            "stat-v1:not-a-time:42",
            out _,
            out _);

        Assert.AreEqual(localFingerprint, utcFingerprint);
        Assert.AreEqual(
            "stat-v1:2026-08-16T12:30:00.0000000Z:42",
            localFingerprint);
        Assert.AreNotEqual(localFingerprint, differentLengthFingerprint);
        StringAssert.Matches(
            localFingerprint,
            new Regex(
                "^stat-v1:[0-9]{4}-[0-9]{2}-[0-9]{2}T" +
                    "[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}Z:" +
                    "(0|[1-9][0-9]*)$",
                RegexOptions.CultureInvariant));
        Assert.IsFalse(hasIncompleteFingerprint);
        Assert.IsNull(incompleteFingerprint);
        Assert.IsTrue(parsed);
        Assert.AreEqual(42, parsedLength);
        Assert.AreEqual(localTime.ToUniversalTime(), parsedLastModifiedUtc);
        Assert.IsFalse(malformedParsed);
    }

    [TestMethod]
    public async Task ProjectBackupAsyncRemovesConfiguredSourcePrefix()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var sourceStore = new MemoryObjectStore();

        await UploadTextAsync(sourceStore, "live/folder/file.txt", "source content");

        var projectedObjects = await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            SourcePrefix: "live",
            Policy: new FolderPolicy(MirrorArchiveFormatName.Value)
        )));

        var projectedObject = projectedObjects.Single();
        Assert.AreEqual("folder/file.txt", projectedObject.RelativePath);
    }

    [TestMethod]
    public async Task ProjectRestoreAsyncRoundTripsFileContentAndTimestamp()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var expectedLastModifiedUtc = new DateTimeOffset
        (
            2026,
            8,
            24,
            12,
            30,
            6,
            TimeSpan.Zero
        );
        var sourceStore = new MemoryObjectStore
        (
            new MirrorSourceTimeProvider(expectedLastModifiedUtc)
        );
        await UploadTextAsync(sourceStore, "folder/file.txt", "source content");
        var backupArtifact = (await CollectProjectedObjectsAsync(handler.ProjectBackupAsync(new
        (
            sourceStore,
            Policy: new FolderPolicy(MirrorArchiveFormatName.Value)
        )))).Single();

        await using var restoreProjection = await handler.ProjectRestoreAsync(new
        (
            backupArtifact
        ));

        Assert.AreEqual(0, restoreProjection.Directories.Count);
        var restoredObject = restoreProjection.Objects.Single();
        Assert.AreEqual("folder/file.txt", restoredObject.RelativePath);
        Assert.AreEqual(backupArtifact.LastModifiedUtc, restoredObject.LastModifiedUtc);
        await AssertProjectedTextAsync(restoredObject, "source content");
    }

    [TestMethod]
    public async Task ProjectRestoreAsyncConvertsEmptyFolderMarkerToDirectory()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var handler = serviceProvider.GetRequiredService<IArchiveFormatHandler>();
        var markerArtifact = new ArchiveProjectedObject
        (
            $"folder/{ArchiveFolderMarkerFileNames.EmptyFolder}",
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ArchiveObjectContent
                (
                    new MemoryStream([], writable: false)
                ));
            },
            ContentLength: 0,
            ChangeFingerprint: "yabt-empty-v1:present"
        );

        await using var restoreProjection = await handler.ProjectRestoreAsync(new
        (
            markerArtifact
        ));

        Assert.AreEqual(0, restoreProjection.Objects.Count);
        CollectionAssert.AreEqual(
            new[] { "folder" },
            restoreProjection.Directories.ToArray());
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMirrorFormatHandler();

        return services;
    }

    private static async Task UploadTextAsync
    (
        MemoryObjectStore store,
        string key,
        string content
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await store.UploadAsync(
            key,
            stream,
            "text/plain",
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static async Task AssertProjectedTextAsync
    (
        ArchiveProjectedObject projectedObject,
        string expectedContent
    )
    {
        await using var content = await projectedObject.OpenContentAsync(default);
        using var reader = new StreamReader(content.Content, Encoding.UTF8);
        Assert.AreEqual(expectedContent, await reader.ReadToEndAsync());
    }

    private static async Task<IReadOnlyList<ArchiveProjectedObject>> CollectProjectedObjectsAsync
    (
        IAsyncEnumerable<ArchiveProjectedObject> projectedObjects
    )
    {
        var result = new List<ArchiveProjectedObject>();
        await foreach (var projectedObject in projectedObjects)
        {
            result.Add(projectedObject);
        }

        return result;
    }

    private sealed class MirrorSourceTimeProvider(DateTimeOffset _utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class MirrorPolicyCaptureLogger<T>(List<string> _warnings) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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
            if (logLevel == LogLevel.Warning) { _warnings.Add(formatter(state, exception)); }
        }
    }
}
