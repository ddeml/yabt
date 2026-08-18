using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipArchiveFormatProjector
(
    ILogger<ZipArchiveFormatProjector> _logger,
    IOptionsMonitor<ZipArchiveFormatOptions> _options
) : IArchiveFormatProjector
{
    private const int DefaultHashBufferSize = 81_920;

    private static readonly byte[] PackageFingerprintDomain =
        Encoding.UTF8.GetBytes("yabt-zip-change-v1");

    // ZIP entry timestamps cannot represent dates before 1980. When a source provider does not
    // supply a modification time, use this fixed value instead of the current time so repeated
    // projections of unchanged content retain the same identity and ZIP metadata.
    private static readonly DateTimeOffset DefaultLastModifiedUtc = new
    (
        1980,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero
    );

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    public string FormatName => ZipArchiveFormatName.Value;

    public bool ProjectsBesideSourceFolder => true;

    public async IAsyncEnumerable<ArchiveProjectedObject> ProjectAsync
    (
        ArchiveProjectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectAsync));

        ArgumentNullException.ThrowIfNull(request);

        await request.SourceStore.EnsureReadyAsync(cancellationToken);

        var sourceObjects = await ListSourceObjectsAsync(
            request,
            cancellationToken);
        var compressionLevel = _options.CurrentValue.CompressionLevel ?? default;
        ArraySegment<byte>? prebuiltContent = null;
        if (sourceObjects.Any(sourceObject => sourceObject.ChangeFingerprint is null))
        {
            // A deterministic package name needs the missing content fingerprints. Build the ZIP
            // while calculating them so no source object must be opened once for naming and again
            // for packaging.
            var prebuiltPackage = await BuildPackageAsync
            (
                request.SourceStore,
                sourceObjects,
                compressionLevel,
                cancellationToken
            );
            sourceObjects = prebuiltPackage.SourceObjects;
            using (prebuiltPackage.Content)
            {
                if (!prebuiltPackage.Content.TryGetBuffer(out var packageBuffer))
                {
                    throw new YabtFormatZipException(
                        "The prebuilt ZIP package did not expose its in-memory buffer.");
                }

                prebuiltContent = packageBuffer;
            }
        }

        var packageFingerprint = ComputePackageFingerprint(
            sourceObjects,
            compressionLevel);
        var packageName = CreatePackageName(
            request.SourceDisplayName,
            request.SourcePrefix,
            packageFingerprint.FileNameToken);

        //TODO: Project the adjacent manifest as a second object once manifest canonicalization is finalized.
        var packageObject = CreatePackageObject
        (
            packageName,
            request.SourceStore,
            sourceObjects,
            compressionLevel,
            packageFingerprint.ChangeFingerprint,
            prebuiltContent
        );

        yield return packageObject;
    }

    private async Task<IReadOnlyList<ZipSourceObject>> ListSourceObjectsAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken
    )
    {
        var sourcePrefix = ArchiveLayout.NormalizeObjectPrefix(request.SourcePrefix);
        var sourceObjects = new List<ZipSourceObject>();
        await AddFolderSourceObjectsAsync
        (
            request.SourceStore,
            sourcePrefix,
            sourcePrefix,
            sourceObjects,
            cancellationToken
        );

        return sourceObjects
            .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task AddFolderSourceObjectsAsync
    (
        IReadOnlyObjectStore sourceStore,
        string? sourcePrefix,
        string? folderPrefix,
        List<ZipSourceObject> sourceObjects,
        CancellationToken cancellationToken
    )
    {
        var sourceItems = sourceStore.GetFolderItemsAsync(
            folderPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var sourceItem in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceItem.IsFolder)
            {
                await AddFolderSourceObjectsAsync(
                    sourceStore,
                    sourcePrefix,
                    sourceItem.Key,
                    sourceObjects,
                    cancellationToken);
                continue;
            }

            if (sourceItem.Object is null)
            {
                continue;
            }

            var sourceKey = ArchiveLayout.NormalizeObjectKey(sourceItem.Object.Key);
            var relativePath = ArchiveLayout.RemovePrefix(sourceKey, sourcePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            var fingerprintResult = GetSourceObjectFingerprint(sourceItem.Object);

            sourceObjects.Add(new
            (
                sourceKey,
                relativePath,
                fingerprintResult.Length,
                sourceItem.Object.LastModifiedUtc?.ToUniversalTime() ?? DefaultLastModifiedUtc,
                fingerprintResult.ChangeFingerprint
            ));
        }
    }

    private static ZipSourceObjectFingerprintResult GetSourceObjectFingerprint
    (
        ArchiveObjectInfo sourceObject
    )
    {
        if (!string.IsNullOrWhiteSpace(sourceObject.ChangeFingerprint))
        {
            return new
            (
                sourceObject.ContentLength,
                sourceObject.ChangeFingerprint
            );
        }

        if (ArchiveChangeFingerprint.TryCreate(
                sourceObject.ContentLength,
                sourceObject.LastModifiedUtc,
                out var changeFingerprint))
        {
            return new
            (
                sourceObject.ContentLength,
                changeFingerprint
            );
        }

        if (!string.IsNullOrWhiteSpace(sourceObject.ContentHash))
        {
            return new
            (
                sourceObject.ContentLength,
                sourceObject.ContentHash
            );
        }

        return new
        (
            sourceObject.ContentLength,
            ChangeFingerprint: null
        );
    }

    private async Task<ZipPackageBuildResult> BuildPackageAsync
    (
        IReadOnlyObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
        CompressionLevel compressionLevel,
        CancellationToken cancellationToken
    )
    {
        var package = new MemoryStream();
        var completedSourceObjects = new List<ZipSourceObject>(sourceObjects.Count);
        byte[]? hashBuffer = null;
        try
        {
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var sourceObject in sourceObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entry = archive.CreateEntry
                    (
                        sourceObject.RelativePath,
                        compressionLevel
                    );
                    entry.LastWriteTime = sourceObject.LastModifiedUtc.ToUniversalTime();

                    await using var sourceContent = await sourceStore.OpenReadAsync(
                        sourceObject.SourceKey,
                        cancellationToken);
                    await using var entryContent = entry.Open();
                    if (sourceObject.ChangeFingerprint is not null)
                    {
                        await sourceContent.Content.CopyToAsync(entryContent, cancellationToken);
                        completedSourceObjects.Add(sourceObject);
                        continue;
                    }

                    var hash = new XxHash128();
                    hashBuffer ??= new byte[GetEffectiveHashBufferSize()];
                    long length = 0;
                    while (true)
                    {
                        var bytesRead = await sourceContent.Content.ReadAsync(
                            hashBuffer,
                            cancellationToken);
                        if (bytesRead == 0) { break; }

                        hash.Append(hashBuffer.AsSpan(0, bytesRead));
                        length += bytesRead;
                        await entryContent.WriteAsync(
                            hashBuffer.AsMemory(0, bytesRead),
                            cancellationToken);
                    }

                    completedSourceObjects.Add(sourceObject with
                    {
                        Length = length,
                        ChangeFingerprint = ArchiveHash.Format(hash.GetHashAndReset()),
                    });
                }
            }

            package.Position = 0;
            return new
            (
                package,
                completedSourceObjects
            );
        }
        catch (Exception)
        {
            await package.DisposeAsync();
            throw;
        }
    }

    private ArchiveProjectedObject CreatePackageObject
    (
        string packageName,
        IReadOnlyObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
        CompressionLevel compressionLevel,
        string packageChangeFingerprint,
        ArraySegment<byte>? prebuiltContent
    )
    {
        async Task<ArchiveObjectContent> OpenPackageAsync(CancellationToken cancellationToken)
        {
            if (prebuiltContent is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = prebuiltContent.Value;
                return new
                (
                    new MemoryStream(
                        content.Array ??
                            throw new YabtFormatZipException(
                                "The prebuilt ZIP package buffer is unavailable."),
                        content.Offset,
                        content.Count,
                        writable: false,
                        publiclyVisible: false),
                    "application/zip",
                    EmptyMetadata
                );
            }

            var package = await BuildPackageAsync
            (
                sourceStore,
                sourceObjects,
                compressionLevel,
                cancellationToken
            );
            return new
            (
                package.Content,
                "application/zip",
                EmptyMetadata
            );
        }

        return new
        (
            packageName,
            OpenPackageAsync,
            // The change fingerprint identifies logical inputs, not the finished ZIP bytes.
            // Leave ContentHash unset so full verification can compare the actual package streams.
            ContentLength: prebuiltContent?.Count,
            ContentHash: null,
            ChangeFingerprint: packageChangeFingerprint
        );
    }

    private int GetEffectiveHashBufferSize()
    {
        var hashBufferSize = _options.CurrentValue.HashBufferSize ?? DefaultHashBufferSize;
        if (hashBufferSize <= 0)
        {
            throw new YabtFormatZipException("Zip archive format hash buffer size must be greater than zero.");
        }

        return hashBufferSize;
    }

    private static ZipPackageFingerprint ComputePackageFingerprint
    (
        IReadOnlyList<ZipSourceObject> sourceObjects,
        CompressionLevel compressionLevel
    )
    {
        var hash = new XxHash128();
        hash.Append(PackageFingerprintDomain);

        Span<byte> compressionLevelValue = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(
            compressionLevelValue,
            (int)compressionLevel);
        hash.Append(compressionLevelValue);

        foreach (var sourceObject in sourceObjects)
        {
            AppendCanonicalString(hash, sourceObject.RelativePath);
            AppendCanonicalNullableInt64(hash, sourceObject.Length);
            AppendCanonicalInt64(hash, sourceObject.LastModifiedUtc.UtcDateTime.Ticks);
            AppendCanonicalString(
                hash,
                sourceObject.ChangeFingerprint ??
                    throw new InvalidOperationException(
                        $"ZIP source object '{sourceObject.SourceKey}' has no change fingerprint."));
        }

        var hashValue = hash.GetHashAndReset();
        return new
        (
            ArchiveHash.Format(hashValue),
            ArchiveHash.FormatFileNameToken(hashValue)
        );
    }

    private static void AppendCanonicalString(XxHash128 hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.Append(length);
        hash.Append(bytes);
    }

    private static void AppendCanonicalNullableInt64(XxHash128 hash, long? value)
    {
        Span<byte> hasValue = stackalloc byte[1];
        hasValue[0] = value.HasValue ? (byte)1 : (byte)0;
        hash.Append(hasValue);
        if (value.HasValue)
        {
            AppendCanonicalInt64(hash, value.Value);
        }
    }

    private static void AppendCanonicalInt64(XxHash128 hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.Append(bytes);
    }

    private static string CreatePackageName
    (
        string? sourceDisplayName,
        string? sourcePrefix,
        string fileNameHash
    )
    {
        var sourceName = Path.GetFileName(
            ArchiveLayout.NormalizeObjectKey(sourceDisplayName ?? sourcePrefix));
        var safeSourceName = SanitizeFileName(string.IsNullOrWhiteSpace(sourceName) ? "root" : sourceName);

        return $"{safeSourceName}.{fileNameHash}.zip";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private sealed record ZipSourceObject
    (
        string SourceKey,
        string RelativePath,
        long? Length,
        DateTimeOffset LastModifiedUtc,
        string? ChangeFingerprint
    );

    private sealed record ZipSourceObjectFingerprintResult
    (
        long? Length,
        string? ChangeFingerprint
    );

    private sealed record ZipPackageFingerprint
    (
        string ChangeFingerprint,
        string FileNameToken
    );

    private sealed record ZipPackageBuildResult
    (
        MemoryStream Content,
        IReadOnlyList<ZipSourceObject> SourceObjects
    );
}
