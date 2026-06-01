using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipArchiveFormatProjector
(
    ILogger<ZipArchiveFormatProjector> _logger,
    IOptionsMonitor<ZipArchiveFormatOptions> _options,
    TimeProvider _timeProvider
) : IArchiveFormatProjector
{
    private const int DefaultHashBufferSize = 81_920;

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    public string FormatName => ZipArchiveFormatName.Value;

    public async Task<ArchiveProjection> ProjectAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectAsync));

        ArgumentNullException.ThrowIfNull(request);

        await request.SourceStore.EnsureReadyAsync(cancellationToken);

        var createdAtUtc = _timeProvider.GetUtcNow();
        var sourceObjects = await ListSourceObjectsAsync(
            request,
            createdAtUtc,
            cancellationToken);
        var manifestHash = ComputeManifestHash(sourceObjects);
        var packageName = CreatePackageName(
            request.SourceDisplayName,
            request.SourcePrefix,
            createdAtUtc,
            manifestHash);

        //TODO: Project the adjacent manifest as a second object once manifest canonicalization is finalized.
        var packageObject = CreatePackageObject
        (
            packageName,
            request.SourceStore,
            sourceObjects,
            manifestHash
        );

        return new([packageObject]);
    }

    private async Task<IReadOnlyList<ZipSourceObject>> ListSourceObjectsAsync
    (
        ArchiveProjectionRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken
    )
    {
        var sourcePrefix = ArchiveLayout.NormalizeObjectPrefix(request.SourcePrefix);
        var sourceObjects = new List<ZipSourceObject>();
        var listedSourceObjects = request.SourceStore.ListAsync
        (
            sourcePrefix,
            cancellationToken
        );

        await foreach (var sourceObject in listedSourceObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceKey = ArchiveLayout.NormalizeObjectKey(sourceObject.Key);
            var relativePath = ArchiveLayout.RemovePrefix(sourceKey, sourcePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            //TODO: Replace eager per-file hashing with the shared manifest hashing pipeline.
            var hashResult = string.IsNullOrWhiteSpace(sourceObject.ContentHash) ?
                await ComputeSourceObjectHashAsync(
                    request.SourceStore,
                    sourceKey,
                    cancellationToken) :
                new ZipSourceObjectHashResult
                (
                    sourceObject.ContentLength,
                    sourceObject.ContentHash
                );

            sourceObjects.Add(new
            (
                sourceKey,
                relativePath,
                hashResult.Length,
                sourceObject.LastModifiedUtc ?? createdAtUtc,
                hashResult.ContentHash
            ));
        }

        return sourceObjects
            .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<ArchiveObjectContent> BuildPackageAsync
    (
        IObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
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
                    _options.CurrentValue.CompressionLevel ?? default
                );
                entry.LastWriteTime = sourceObject.LastModifiedUtc;

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
        IObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
        string manifestHash
    ) => new
    (
        packageName,
        cancellationToken => BuildPackageAsync
        (
            sourceStore,
            sourceObjects,
            cancellationToken
        ),
        ContentHash: manifestHash
    );

    private async Task<ZipSourceObjectHashResult> ComputeSourceObjectHashAsync
    (
        IObjectStore sourceStore,
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
            ToContentHash(hash.GetHashAndReset())
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

    private static string ComputeManifestHash(IReadOnlyList<ZipSourceObject> sourceObjects)
    {
        var hash = new XxHash128();

        foreach (var sourceObject in sourceObjects)
        {
            var length = sourceObject.Length?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var line =
                $"{sourceObject.RelativePath}\t{length}\t" +
                $"{sourceObject.LastModifiedUtc.UtcDateTime:O}\t{sourceObject.ContentHash}\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            hash.Append(bytes);
        }

        return ToContentHash(hash.GetHashAndReset());
    }

    private static string CreatePackageName
    (
        string? sourceDisplayName,
        string? sourcePrefix,
        DateTimeOffset createdAtUtc,
        string manifestHash
    )
    {
        var sourceName = Path.GetFileName(
            ArchiveLayout.NormalizeObjectKey(sourceDisplayName ?? sourcePrefix));
        var safeSourceName = SanitizeFileName(string.IsNullOrWhiteSpace(sourceName) ? "root" : sourceName);
        var normalizedHash = NormalizeHash(manifestHash);
        var hashPrefix = normalizedHash.Length <= 8 ? normalizedHash : normalizedHash[..8];

        return $"{safeSourceName}.{createdAtUtc:yyyyMMddTHHmmssZ}.{hashPrefix}.zip";
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

    private static string NormalizeHash(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? value : value[(separator + 1)..];
    }

    private static string ToContentHash(byte[] hash)
    {
        return $"xxh128:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private sealed record ZipSourceObject
    (
        string SourceKey,
        string RelativePath,
        long? Length,
        DateTimeOffset LastModifiedUtc,
        string ContentHash
    );

    private sealed record ZipSourceObjectHashResult
    (
        long? Length,
        string ContentHash
    );
}
