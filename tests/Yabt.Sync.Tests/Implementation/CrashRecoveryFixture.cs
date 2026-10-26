using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Metadata;

namespace Yabt.Sync.Tests.Implementation;

internal sealed class CrashRecoveryFixture(string rootPath) : IDisposable
{
    private const string DirectoryPrefix = "yabt-crash-recovery-";
    private const string OwnershipFileName = ".crash-fixture-owner";
    private static readonly UTF8Encoding Utf8 = new(false);
    private string? _ownershipToken;

    public string RootPath { get; } = Path.GetFullPath(rootPath);

    public string SourceRoot => Path.Combine(RootPath, "source");

    public string ArchiveRoot => Path.Combine(RootPath, "archive");

    public string DestinationRoot => Path.Combine(RootPath, "restored");

    public string ProgressDirectory => Path.Combine(RootPath, "progress");

    public string ScratchRoot => Path.Combine(RootPath, "scratch");

    public static async Task<CrashRecoveryFixture> CreateAsync()
    {
        var token = Guid.NewGuid().ToString("N");
        var fixture = new CrashRecoveryFixture(Path.Combine(Path.GetTempPath(), DirectoryPrefix + token))
        {
            _ownershipToken = token,
        };
        Directory.CreateDirectory(fixture.RootPath);
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, OwnershipFileName), token, Utf8);
        Directory.CreateDirectory(fixture.SourceRoot);
        Directory.CreateDirectory(fixture.ArchiveRoot);
        Directory.CreateDirectory(fixture.DestinationRoot);
        Directory.CreateDirectory(fixture.ProgressDirectory);
        Directory.CreateDirectory(fixture.ScratchRoot);

        var store = new BackupRootStore("archive", FileSystemObjectStoreKind.Value)
        {
            ProviderProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["rootPath"] = JsonSerializer.SerializeToElement(fixture.ArchiveRoot),
            },
        };
        var descriptor = new BackupRootDescriptor
        (
            BackupRootDescriptor.ExpectedDocumentType,
            BackupRootDescriptor.ExpectedSchemaVersion,
            "crash-recovery-" + token,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            [store],
            RootRole: "source",
            DefaultStoreId: store.Id,
            ChangeManifestCompression: "none"
        );
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await fixture.WriteSourceAsync
        (
            BackupRootFileNames.Primary, JsonSerializer.Serialize(descriptor, jsonOptions)
        );
        await fixture.WriteSourceAsync(FolderPolicyFileNames.Primary, "{\"format\":\"mirror\"}");
        return fixture;
    }

    public ServiceProvider CreateServices(CrashRecoveryGate? gate = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["TemporaryContent:TemporaryDirectory"] = ScratchRoot,
                ["Sync:ProgressDirectory"] = ProgressDirectory,
                ["Sync:ProgressEnabled"] = "true",
                ["Sync:ProgressFlushUnitCount"] = int.MaxValue.ToString(CultureInfo.InvariantCulture),
                ["Sync:ProgressFlushIntervalSeconds"] = "3600",
                ["Sync:RestoreMaximumConcurrency"] = "1",
            }
        ).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (gate is null)
        {
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        }
        else
        {
            services.AddSingleton(gate);
            services.AddSingleton(typeof(ILogger<>), typeof(CrashRecoveryLogger<>));
        }
        services.AddYabtMetadata();
        services.AddYabtFileSystemObjectStore();
        services.AddYabtMirrorFormatHandler();
        services.AddYabtZipFormatHandler();
        services.AddYabtTemporaryContent("TemporaryContent");
        services.AddYabtSync("Sync");
        return services.BuildServiceProvider();
    }

    public async Task<SyncRunResult> BackupAsync(CrashRecoveryGate? gate = null)
    {
        await using var services = CreateServices(gate);
        return await services.GetRequiredService<IArchiveSynchronizer>().BackupAsync(new(SourceRoot));
    }

    public async Task<SyncRunResult> RestoreAsync(CrashRecoveryGate? gate = null)
    {
        await using var services = CreateServices(gate);
        return await services.GetRequiredService<IArchiveSynchronizer>().RestoreAsync
        (
            new(SourceRoot, DestinationRoot: DestinationRoot)
        );
    }

    public async Task WriteSourceAsync(string relativePath, string content)
    {
        var path = PathFor(SourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var timestamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).AddSeconds(1) :
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await File.WriteAllTextAsync(path, content, Utf8);
        File.SetLastWriteTimeUtc(path, timestamp);
    }

    public static string PathFor(string root, string relativePath) =>
        Path.Combine(root, ObjectPath.Parse(relativePath).Value.Replace('/', Path.DirectorySeparatorChar));

    public static void AssertText(string root, string relativePath, string expected)
    {
        var path = PathFor(root, relativePath);
        Assert.IsTrue(File.Exists(path), $"Expected file '{path}' is absent.");
        CollectionAssert.AreEqual(Utf8.GetBytes(expected), File.ReadAllBytes(path), path);
    }

    public static void AssertHistoryText(string root, string relativePath, string expected)
    {
        var historyRoot = PathFor(root, ArchiveLayout.Default.HistPrefix);
        Assert.IsTrue(Directory.Exists(historyRoot), $"Expected history root '{historyRoot}' is absent.");
        var expectedBytes = Utf8.GetBytes(expected);
        foreach (var path in Directory.EnumerateFiles(historyRoot, "*", SearchOption.AllDirectories))
        {
            var historyRelativePath = Path.GetRelativePath(historyRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var timestampSeparator = historyRelativePath.IndexOf('/');
            if
            (
                timestampSeparator >= 0 &&
                historyRelativePath[(timestampSeparator + 1)..] == relativePath &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(expectedBytes)
            )
            {
                return;
            }
        }
        Assert.Fail($"History contains no exact occurrence of '{relativePath}' with the expected bytes.");
    }

    public void Dispose()
    {
        if (_ownershipToken is null || !Directory.Exists(RootPath)) { return; }
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), DirectoryPrefix + _ownershipToken));
        var ownerPath = Path.Combine(RootPath, OwnershipFileName);
        if
        (
            !string.Equals(RootPath, expectedRoot, StringComparison.Ordinal) ||
            (File.GetAttributes(RootPath) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(ownerPath) || File.ReadAllText(ownerPath, Utf8) != _ownershipToken
        )
        {
            throw new IOException("Refusing to remove a crash fixture whose ownership no longer matches.");
        }
        Directory.Delete(RootPath, recursive: true);
        _ownershipToken = null;
    }
}
