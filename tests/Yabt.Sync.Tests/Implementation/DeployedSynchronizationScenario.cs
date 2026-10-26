using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Metadata;

namespace Yabt.Sync.Tests.Implementation;

internal sealed class DeployedSynchronizationScenario : IDisposable
{
    private const string WorkspacePrefix = "yabt-deployed-sync-";
    private const string OwnershipFile = ".deployed-fixture-owner";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTime InitialTimestamp =
        new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567);

    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly string _workspace;
    private readonly string _source;
    private readonly string _destination;
    private readonly string _scratch;
    private readonly string _progress;

    private DeployedSynchronizationScenario()
    {
        _workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), WorkspacePrefix + _token));
        _source = Path.Combine(_workspace, "source");
        _destination = Path.Combine(_workspace, "restored");
        _scratch = Path.Combine(_workspace, "scratch");
        _progress = Path.Combine(_workspace, "progress");
        Directory.CreateDirectory(_workspace);
        File.WriteAllText(Path.Combine(_workspace, OwnershipFile), _token, Utf8);
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_scratch);
        Directory.CreateDirectory(_progress);
    }

    public static async Task ExecuteAsync
    (
        IArchiveMutableObjectStore archive,
        string providerKind,
        string format
    )
    {
        using var scenario = new DeployedSynchronizationScenario();
        await scenario.RunAsync(archive, providerKind, format);
    }

    private async Task RunAsync(IArchiveMutableObjectStore archive, string providerKind, string format)
    {
        await InitializeSourceAsync(providerKind, format);
        await WriteSourceAsync("unchanged.txt", "unchanged payload", InitialTimestamp);
        await WriteSourceAsync("updated.txt", "original payload", InitialTimestamp);
        await WriteSourceAsync("deleted.txt", "deleted payload", InitialTimestamp);
        await WriteSourceAsync("nested/résumé #100%.txt", "nested Unicode payload", InitialTimestamp);
        Directory.CreateDirectory(Path.Combine(_source, "empty"));

        await using var services = CreateServices(archive, providerKind);
        var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();
        AssertCompleted(await synchronizer.BackupAsync(new(_source)), "initial backup");
        var originalArtifacts = await ReadOriginalArtifactsAsync(archive, format);

        var unchanged = await synchronizer.BackupAsync(new(_source));
        AssertCompleted(unchanged, "unchanged backup");
        Assert.AreEqual(0, unchanged.NewCount, "An unchanged backup created content.");
        Assert.AreEqual(0, unchanged.ChangedCount, "An unchanged backup replaced content.");
        Assert.AreEqual(0, unchanged.ExtraCount, "An unchanged backup removed content.");

        AssertCompleted
        (
            await synchronizer.RestoreAsync(new(_source, DestinationRoot: _destination)),
            "initial restore"
        );
        await AssertRestoredAsync(changed: false);

        await WriteSourceAsync("updated.txt", "replacement payload", InitialTimestamp.AddSeconds(10));
        File.Delete(Path.Combine(_source, "deleted.txt"));
        await WriteSourceAsync("added.txt", "new payload", InitialTimestamp.AddSeconds(20));

        AssertCompleted(await synchronizer.BackupAsync(new(_source)), "changed backup");
        AssertCompleted
        (
            await synchronizer.VerifyAsync(new(_source, ByteForByte: true)),
            "byte-for-byte archive verification"
        );
        await AssertHistoryAsync(archive, originalArtifacts);
        AssertCompleted
        (
            await synchronizer.RestoreAsync(new(_source, DestinationRoot: _destination)),
            "changed restore"
        );
        await AssertRestoredAsync(changed: true);
        AssertLocalHistory("updated.txt", "original payload");
        AssertLocalHistory("deleted.txt", "deleted payload");
        Assert.IsFalse(await archive.ExistsAsync(ArchiveChangeManifest.InvalidationMarkerFileName));
        Assert.IsFalse(await archive.ExistsAsync(ArchiveInternalObjectKeys.MutationLock));
    }

    private async Task InitializeSourceAsync(string providerKind, string format)
    {
        var store = new BackupRootStore("acceptance", providerKind);
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            BackupRootDescriptor.ExpectedSchemaVersion,
            "deployed-service-" + _token,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            [store],
            RootRole: "source",
            DefaultStoreId: store.Id,
            ChangeManifestCompression: "none"
        );
        await WriteSourceAsync
        (
            BackupRootFileNames.Primary,
            JsonSerializer.Serialize
            (
                descriptor,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }
            ),
            InitialTimestamp
        );
        await WriteSourceAsync(FolderPolicyFileNames.Primary, "{\"format\":\"" + format + "\"}", InitialTimestamp);
    }

    private ServiceProvider CreateServices(IArchiveMutableObjectStore archive, string providerKind)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["TemporaryContent:TemporaryDirectory"] = _scratch,
                ["TemporaryContent:MemoryThresholdBytes"] = "0",
                ["Sync:ProgressDirectory"] = _progress,
                ["Sync:ProgressEnabled"] = "true",
                ["Sync:RestoreMaximumConcurrency"] = "1",
            }
        ).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtFileSystemObjectStore();
        services.AddYabtMirrorFormatHandler();
        services.AddYabtZipFormatHandler();
        services.AddYabtTemporaryContent("TemporaryContent");
        services.AddYabtSync("Sync");
        // The already configured fixture supplies the remote provider. This
        // exercises synchronization, not production CLI provider resolution.
        services.AddSingleton<IBackupRootStoreResolver>
        (
            new DeployedBackupRootStoreResolver(providerKind, archive)
        );
        return services.BuildServiceProvider();
    }

    private static async Task<Dictionary<string, byte[]>> ReadOriginalArtifactsAsync
    (
        IReadOnlyObjectStore archive,
        string format
    )
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (format == "mirror")
        {
            result.Add("updated.txt", Utf8.GetBytes("original payload"));
            result.Add("deleted.txt", Utf8.GetBytes("deleted payload"));
            return result;
        }

        await foreach (var item in archive.GetFolderItemsAsync(null))
        {
            if (item.IsObject &&
                (item.Key.EndsWith(".zip", StringComparison.Ordinal) ||
                 item.Key.EndsWith(".zip.yabt-manifest.json", StringComparison.Ordinal)))
            {
                result.Add(item.Key, await ReadSmallArtifactAsync(archive, item.Key));
            }
        }
        Assert.HasCount(2, result, "The ZIP root must produce its package and adjacent manifest.");
        return result;
    }

    private static async Task AssertHistoryAsync
    (
        IReadOnlyObjectStore archive,
        IReadOnlyDictionary<string, byte[]> originals
    )
    {
        var history = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var historyPrefix = ArchiveLayout.Default.HistPrefix + "/";
        await foreach (var item in archive.GetFolderItemsAsync(ArchiveLayout.Default.HistPrefix, recursive: true))
        {
            if (!item.IsObject || !item.Key.StartsWith(historyPrefix, StringComparison.Ordinal)) { continue; }
            var occurrence = item.Key[historyPrefix.Length..];
            var separator = occurrence.IndexOf('/');
            if (separator < 0) { continue; }
            var originalKey = occurrence[(separator + 1)..];
            if (!history.TryGetValue(originalKey, out var candidates))
            {
                history.Add(originalKey, candidates = []);
            }
            candidates.Add(item.Key);
        }

        foreach (var (key, expected) in originals)
        {
            Assert.IsTrue(history.TryGetValue(key, out var candidates), "Original archive content was not historized.");
            Assert.IsNotNull(candidates);
            var found = false;
            foreach (var candidate in candidates)
            {
                var actual = await ReadSmallArtifactAsync(archive, candidate);
                if (actual.AsSpan().SequenceEqual(expected)) { found = true; break; }
            }
            Assert.IsTrue(found, "Historized bytes differ from the original archive artifact.");
        }
    }

    private async Task AssertRestoredAsync(bool changed)
    {
        var relativePaths = new List<string>
        {
            BackupRootFileNames.Primary, FolderPolicyFileNames.Primary,
            "unchanged.txt", "updated.txt", "nested/résumé #100%.txt",
            changed ? "added.txt" : "deleted.txt",
        };
        foreach (var relativePath in relativePaths)
        {
            var sourcePath = LocalPath(_source, relativePath);
            var restoredPath = LocalPath(_destination, relativePath);
            CollectionAssert.AreEqual
            (
                await File.ReadAllBytesAsync(sourcePath),
                await File.ReadAllBytesAsync(restoredPath),
                "Restored file bytes differ from the source."
            );
            if (relativePath != BackupRootFileNames.Primary)
            {
                Assert.AreEqual
                (
                    File.GetLastWriteTimeUtc(sourcePath),
                    File.GetLastWriteTimeUtc(restoredPath),
                    "Restore must preserve exact source modification times."
                );
            }
        }
        Assert.IsTrue(Directory.Exists(Path.Combine(_destination, "empty")));
        Assert.IsFalse(File.Exists(Path.Combine(_destination, "empty", ".yabt-empty")));
        if (changed) { Assert.IsFalse(File.Exists(Path.Combine(_destination, "deleted.txt"))); }
    }

    private void AssertLocalHistory(string relativePath, string content)
    {
        var historyRoot = LocalPath(_destination, ArchiveLayout.Default.HistPrefix);
        Assert.IsTrue(Directory.Exists(historyRoot), "Restoring changed content must preserve destination history.");
        foreach (var timestampRoot in Directory.EnumerateDirectories(historyRoot))
        {
            var candidate = LocalPath(timestampRoot, relativePath);
            if (File.Exists(candidate) && File.ReadAllText(candidate, Utf8) == content) { return; }
        }
        Assert.Fail("Restore did not retain the previous destination bytes in history.");
    }

    private async Task WriteSourceAsync(string relativePath, string content, DateTime timestamp)
    {
        var path = LocalPath(_source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Utf8);
        File.SetLastWriteTimeUtc(path, timestamp);
    }

    private static async Task<byte[]> ReadSmallArtifactAsync(IReadOnlyObjectStore store, string key)
    {
        await using var content = await store.OpenReadAsync(key);
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer);
        // This test constructs only a handful of fixed, small payloads.
        return buffer.ToArray();
    }

    private static string LocalPath(string root, string relativePath) =>
        Path.Combine(root, ObjectPath.Parse(relativePath).Value.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertCompleted(SyncRunResult result, string operation)
    {
        Assert.IsTrue(result.Completed, operation + " did not complete.");
        Assert.AreEqual(0, result.FailedCount, operation + " reported item failures.");
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), WorkspacePrefix + _token));
        if (!string.Equals(_workspace, expected, StringComparison.Ordinal) ||
            (File.GetAttributes(_workspace) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(Path.Combine(_workspace, OwnershipFile), Utf8) != _token)
        {
            throw new IOException("Refusing to remove a deployed-service fixture with changed ownership.");
        }
        Directory.Delete(_workspace, recursive: true);
    }
}
