using System.Globalization;
using System.Text.Json;
using Yabt.Core.Models;
using Yabt.FileSystem;
using Yabt.Format.Mirror;
using Yabt.Format.Zip;
using Yabt.Metadata;
using Yabt.Sync;

namespace Yabt.Profiling.Implementation;

internal static class ProfilingFixtures
{
    private const string ManifestFileName = ".yabt-profiling-fixture.json";
    private const string DocumentType = "yabt.profilingFixture";
    private const int ManifestMaximumBytes = 4096;
    private const int GroupSize = 100;
    private const int PayloadLength = 32;
    private static readonly DateTime TimestampUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<ProfilingFixture> CreateSourceAsync
    (
        string rootPath,
        int fileCount,
        string shape,
        string format,
        CancellationToken cancellationToken = default
    )
    {
        ValidateParameters(fileCount, shape, format);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRootPath = Path.GetFullPath(rootPath);
        var parentPath = Path.GetDirectoryName(fullRootPath);
        if (parentPath is null || !Directory.Exists(parentPath))
        {
            throw new IOException("The profiling fixture's parent directory must already exist.");
        }

        RequireMissingPath(fullRootPath);
        Directory.CreateDirectory(fullRootPath);
        var fixture = new ProfilingFixture(fullRootPath, fileCount, shape, format);
        await WriteManifestAsync(fixture, "creating", createNew: true, cancellationToken);
        Directory.CreateDirectory(fixture.SourceRoot);
        Directory.CreateDirectory(fixture.ArchiveRoot);
        Directory.CreateDirectory(fixture.ScratchRoot);
        Directory.CreateDirectory(fixture.DestinationsRoot);

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
            $"profiling-{Guid.NewGuid():N}",
            new DateTimeOffset(TimestampUtc),
            ArchiveLayout.Default,
            [store],
            RootRole: "source",
            DefaultStoreId: store.Id
        );
        await WriteJsonAsync
        (
            Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary),
            descriptor,
            cancellationToken
        );
        await WriteJsonAsync
        (
            Path.Combine(fixture.SourceRoot, FolderPolicyFileNames.Primary),
            new FolderPolicy(format),
            cancellationToken
        );

        var payload = new byte[PayloadLength];
        for (var index = 0; index < fileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FillPayload(payload, index, changed: false);
            await WritePayloadAsync
            (
                GetPayloadPath(fixture, fixture.SourceRoot, index),
                payload,
                TimestampUtc,
                cancellationToken
            );
        }

        File.SetLastWriteTimeUtc(Path.Combine(fixture.SourceRoot, FolderPolicyFileNames.Primary), TimestampUtc);
        File.SetLastWriteTimeUtc(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary), TimestampUtc);
        await WriteManifestAsync(fixture, "source-ready", createNew: false, cancellationToken);
        return fixture;
    }

    public static async Task PrepareArchiveAsync
    (
        ProfilingFixture fixture,
        IArchiveSynchronizer synchronizer,
        CancellationToken cancellationToken = default
    )
    {
        await ValidateOwnedFixtureAsync(fixture, "source-ready", cancellationToken);
        var result = await synchronizer.BackupAsync(new(fixture.SourceRoot), cancellationToken);
        RequireCompleted(result, "Preparing the profiling archive");
        await WriteManifestAsync(fixture, "ready", createNew: false, cancellationToken);
    }

    public static async Task<ProfilingFixture> LoadAsync
    (
        string rootPath,
        CancellationToken cancellationToken = default
    )
    {
        var fullRootPath = Path.GetFullPath(rootPath);
        var manifest = await ReadManifestAsync(fullRootPath, cancellationToken);
        ValidateParameters(manifest.FileCount, manifest.Shape, manifest.Format);
        var fixture = new ProfilingFixture
        (
            fullRootPath,
            manifest.FileCount,
            manifest.Shape,
            manifest.Format
        );
        ValidateOwnedFixture(fixture, manifest, "ready");
        RequireOrdinaryFile(Path.Combine(fixture.SourceRoot, BackupRootFileNames.Primary));
        RequireOrdinaryFile(Path.Combine(fixture.ArchiveRoot, BackupRootFileNames.Primary));
        return fixture;
    }

    public static async Task<string> PrepareDestinationAsync
    (
        ProfilingFixture fixture,
        string scenario,
        string destinationName,
        IArchiveSynchronizer synchronizer,
        CancellationToken cancellationToken = default
    )
    {
        if (scenario is not ("empty" or "unchanged" or "mixed"))
        {
            throw new ArgumentException("Destination scenario must be empty, unchanged, or mixed.", nameof(scenario));
        }

        if
        (
            string.IsNullOrEmpty(destinationName) || destinationName.Length > 64 ||
            destinationName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
        )
        {
            throw new ArgumentException
            (
                "Destination names must contain 1 to 64 ASCII letters, digits, hyphens, or underscores.",
                nameof(destinationName)
            );
        }

        await ValidateOwnedFixtureAsync(fixture, "ready", cancellationToken);
        var destinationRoot = Path.Combine(fixture.DestinationsRoot, destinationName);
        RequireMissingPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        if (scenario == "empty") { return destinationRoot; }

        var result = await synchronizer.RestoreAsync
        (
            new(fixture.SourceRoot, DestinationRoot: destinationRoot),
            cancellationToken
        );
        RequireCompleted(result, "Preparing the profiling restore destination");
        if (scenario == "mixed")
        {
            await AlterDestinationAsync(fixture, destinationRoot, cancellationToken);
        }

        return destinationRoot;
    }

    private static async Task AlterDestinationAsync
    (
        ProfilingFixture fixture,
        string destinationRoot,
        CancellationToken cancellationToken
    )
    {
        var payload = new byte[PayloadLength];
        for (var index = 0; index < fixture.FileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetPayloadPath(fixture, destinationRoot, index);
            if (index % 10 == 0)
            {
                RequireOrdinaryFile(path);
                FillPayload(payload, index, changed: true);
                await File.WriteAllBytesAsync(path, payload, cancellationToken);
                File.SetLastWriteTimeUtc(path, TimestampUtc.AddDays(1));
            }
            else if (index % 10 == 1)
            {
                RequireOrdinaryFile(path);
                File.Delete(path);
            }

            if (index % 10 == 2)
            {
                FillPayload(payload, index, changed: true);
                var extraName = $"extra-{index.ToString("D9", CultureInfo.InvariantCulture)}.bin";
                await WritePayloadAsync
                (
                    Path.Combine(Path.GetDirectoryName(path)!, extraName),
                    payload,
                    TimestampUtc,
                    cancellationToken
                );
            }
        }
    }

    private static string GetPayloadPath(ProfilingFixture fixture, string rootPath, int index)
    {
        var fileName = $"file-{index.ToString("D9", CultureInfo.InvariantCulture)}.bin";
        return fixture.Shape == "flat" ?
            Path.Combine(rootPath, fileName) :
            Path.Combine(rootPath, $"dir-{(index / GroupSize).ToString("D9", CultureInfo.InvariantCulture)}", fileName);
    }

    private static void FillPayload(byte[] payload, int index, bool changed)
    {
        for (var offset = 0; offset < payload.Length; offset++)
        {
            payload[offset] = (byte)(((long)index + offset + (changed ? 127 : 0)) % 251);
        }
    }

    private static async Task WritePayloadAsync
    (
        string path,
        byte[] payload,
        DateTime timestampUtc,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(payload, cancellationToken);
        }

        File.SetLastWriteTimeUtc(path, timestampUtc);
    }

    private static async Task WriteJsonAsync<T>
    (
        string path,
        T value,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task WriteManifestAsync
    (
        ProfilingFixture fixture,
        string state,
        bool createNew,
        CancellationToken cancellationToken
    )
    {
        var manifest = new ProfilingFixtureManifest(DocumentType, 1, fixture.FileCount, fixture.Shape, fixture.Format, state);
        var manifestPath = Path.Combine(fixture.RootPath, ManifestFileName);
        if (createNew)
        {
            await WriteJsonAsync(manifestPath, manifest, cancellationToken);
            return;
        }

        var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
        await WriteJsonAsync(temporaryPath, manifest, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private static async Task<ProfilingFixtureManifest> ReadManifestAsync
    (
        string rootPath,
        CancellationToken cancellationToken
    )
    {
        RequireOrdinaryDirectory(rootPath);
        var manifestPath = Path.Combine(rootPath, ManifestFileName);
        RequireOrdinaryFile(manifestPath);
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[ManifestMaximumBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) { break; }
            count += read;
        }

        if (count > ManifestMaximumBytes)
        {
            throw new InvalidDataException("Profiling fixture ownership metadata exceeds its size limit.");
        }

        var manifest = JsonSerializer.Deserialize<ProfilingFixtureManifest>(buffer.AsSpan(0, count), JsonOptions) ??
            throw new InvalidDataException("Profiling fixture ownership metadata is empty.");
        if (manifest.DocumentType != DocumentType || manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException("The path does not contain recognized profiling fixture ownership metadata.");
        }

        return manifest;
    }

    private static async Task ValidateOwnedFixtureAsync
    (
        ProfilingFixture fixture,
        string state,
        CancellationToken cancellationToken
    )
    {
        var manifest = await ReadManifestAsync(fixture.RootPath, cancellationToken);
        ValidateOwnedFixture(fixture, manifest, state);
    }

    private static void ValidateOwnedFixture
    (
        ProfilingFixture fixture,
        ProfilingFixtureManifest manifest,
        string state
    )
    {
        if
        (
            manifest.FileCount != fixture.FileCount || manifest.Shape != fixture.Shape ||
            manifest.Format != fixture.Format || manifest.State != state
        )
        {
            throw new InvalidDataException("Profiling fixture metadata does not match the requested fixture or preparation state.");
        }

        RequireOrdinaryDirectory(fixture.SourceRoot);
        RequireOrdinaryDirectory(fixture.ArchiveRoot);
        RequireOrdinaryDirectory(fixture.ScratchRoot);
        RequireOrdinaryDirectory(fixture.DestinationsRoot);
    }

    private static void ValidateParameters(int fileCount, string shape, string format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileCount);
        if (shape is not ("flat" or "grouped"))
        {
            throw new ArgumentException("Fixture shape must be flat or grouped.", nameof(shape));
        }

        if (format is not (MirrorArchiveFormatName.Value or ZipArchiveFormatName.Value))
        {
            throw new ArgumentException("Fixture format must be mirror or zip.", nameof(format));
        }
    }

    private static void RequireMissingPath(string path)
    {
        if (Path.Exists(path))
        {
            throw new IOException($"Profiling preparation requires a new path and never replaces existing contents: '{path}'.");
        }
    }

    private static void RequireOrdinaryDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Profiling fixture directory is missing or is a link: '{path}'.");
        }
    }

    private static void RequireOrdinaryFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Profiling fixture file is missing or is a link: '{path}'.");
        }
    }

    private static void RequireCompleted(SyncRunResult result, string operation)
    {
        if (!result.Completed)
        {
            throw new IOException($"{operation} failed: {result.Message} ({result.FailedCount} failures).");
        }
    }
}
