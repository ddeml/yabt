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
        var packageChangeFingerprint = ComputePackageChangeFingerprint(
            sourceObjects,
            compressionLevel);
        var packageName = CreatePackageName(
            request.SourceDisplayName,
            request.SourcePrefix,
            packageChangeFingerprint);

        //TODO: Project the adjacent manifest as a second object once manifest canonicalization is finalized.
        var packageObject = CreatePackageObject
        (
            packageName,
            request.SourceStore,
            sourceObjects,
            compressionLevel,
            packageChangeFingerprint
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

            var fingerprintResult = await GetSourceObjectFingerprintAsync(
                sourceStore,
                sourceItem.Object,
                sourceKey,
                cancellationToken);

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

    private async Task<ZipSourceObjectFingerprintResult> GetSourceObjectFingerprintAsync
    (
        IReadOnlyObjectStore sourceStore,
        ArchiveObjectInfo sourceObject,
        string sourceKey,
        CancellationToken cancellationToken
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

        return await ComputeSourceObjectHashAsync(
            sourceStore,
            sourceKey,
            cancellationToken);
    }

    private async Task<ArchiveObjectContent> BuildPackageAsync
    (
        IReadOnlyObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
        CompressionLevel compressionLevel,
        CancellationToken cancellationToken
    )
    {
        var package = new MemoryStream();
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
                await sourceContent.Content.CopyToAsync(entryContent, cancellationToken);
            }
        }

        package.Position = 0;
        return new
        (
            package,
            "application/zip",
            EmptyMetadata
        );
    }

    private ArchiveProjectedObject CreatePackageObject
    (
        string packageName,
        IReadOnlyObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
        CompressionLevel compressionLevel,
        string packageChangeFingerprint
    ) => new
    (
        packageName,
        cancellationToken => BuildPackageAsync
        (
            sourceStore,
            sourceObjects,
            compressionLevel,
            cancellationToken
        ),
        // The change fingerprint identifies logical inputs, not the finished ZIP bytes.
        // Leave ContentHash unset so full verification can compare the actual package streams.
        ContentHash: null,
        ChangeFingerprint: packageChangeFingerprint
    );

    private async Task<ZipSourceObjectFingerprintResult> ComputeSourceObjectHashAsync
    (
        IReadOnlyObjectStore sourceStore,
        string sourceKey,
        CancellationToken cancellationToken
    )
    {
        await using var sourceContent = await sourceStore.OpenReadAsync(
            sourceKey,
            cancellationToken);
        var hash = new XxHash128();
        var buffer = new byte[GetEffectiveHashBufferSize()];
        long length = 0;

        while (true)
        {
            var bytesRead = await sourceContent.Content.ReadAsync(
                buffer,
                cancellationToken);
            if (bytesRead == 0) { break; }

            length += bytesRead;
            hash.Append(buffer.AsSpan(0, bytesRead));
        }

        return new
        (
            length,
            ArchiveHash.Format(hash.GetHashAndReset())
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

    private static string ComputePackageChangeFingerprint
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
            AppendCanonicalString(hash, sourceObject.ChangeFingerprint);
        }

        return ArchiveHash.Format(hash.GetHashAndReset());
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
        string manifestHash
    )
    {
        var sourceName = Path.GetFileName(
            ArchiveLayout.NormalizeObjectKey(sourceDisplayName ?? sourcePrefix));
        var safeSourceName = SanitizeFileName(string.IsNullOrWhiteSpace(sourceName) ? "root" : sourceName);
        var fileNameHash = ToFileNameHash(manifestHash);

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

    private static string ToFileNameHash(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new YabtFormatZipException(
                "ZIP package identity hash must include an algorithm and value.");
        }

        return $"{value[..separator]}-{value[(separator + 1)..]}";
    }

    private sealed record ZipSourceObject
    (
        string SourceKey,
        string RelativePath,
        long? Length,
        DateTimeOffset LastModifiedUtc,
        string ChangeFingerprint
    );

    private sealed record ZipSourceObjectFingerprintResult
    (
        long? Length,
        string ChangeFingerprint
    );
}
