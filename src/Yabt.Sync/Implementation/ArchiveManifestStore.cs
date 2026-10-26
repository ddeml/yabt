using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveManifestStore
(
    ILogger<ArchiveManifestStore> _logger,
    IChangeManifestSerializer _changeManifestSerializer,
    ILogicalStateManifestSerializer _logicalStateManifestSerializer,
    ITemporaryContentFactory _temporaryContentFactory
)
{
    internal static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly byte[] LogicalStateManifestInvalidationMarkerContent = Encoding.UTF8.GetBytes
    (
        "{\"documentType\":\"yabt.logicalStateManifestInvalidation\",\"schemaVersion\":1}\n"
    );

    internal async Task<bool> PrepareRestoreChangeManifestMutationAsync
    (
        IArchiveMutableObjectStore destinationStore,
        bool liveStateChanged,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(PrepareRestoreChangeManifestMutationAsync));

        var invalidationMarkerExists = await destinationStore.ExistsAsync(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            cancellationToken);
        if (!liveStateChanged && !invalidationMarkerExists)
        {
            return false;
        }

        var existingManifestFileNames = new List<string>();
        foreach (var manifestFileName in GetChangeManifestFileNames())
        {
            if (await destinationStore.ExistsAsync(
                    manifestFileName,
                    cancellationToken))
            {
                existingManifestFileNames.Add(manifestFileName);
            }
        }

        if (existingManifestFileNames.Count != 0 && !invalidationMarkerExists)
        {
            await UploadChangeManifestInvalidationMarkerAsync(
                destinationStore,
                cancellationToken);
            invalidationMarkerExists = true;
        }

        await DeleteChangeManifestsAsync(
            destinationStore,
            existingManifestFileNames,
            cancellationToken);
        return invalidationMarkerExists;
    }

    internal async Task<LogicalStateManifestLoad> ReadLogicalStateManifestAsync
    (
        IObjectStore destinationStore,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReadLogicalStateManifestAsync));

        var markerExists = await destinationStore.ExistsAsync(
            ArchiveLogicalStateManifest.InvalidationMarkerFileName,
            cancellationToken);
        var manifestExists = await destinationStore.ExistsAsync(
            ArchiveLogicalStateManifest.FileName,
            cancellationToken);
        if (markerExists || !manifestExists)
        {
            return new(manifestExists, markerExists, Manifest: null);
        }

        try
        {
            _logger.LogControlMetadataOperation(
                "Reading",
                ArchiveLogicalStateManifest.FileName);
            await using var content = await destinationStore.OpenReadAsync(
                ArchiveLogicalStateManifest.FileName,
                cancellationToken);
            var manifest = await _logicalStateManifestSerializer.ReadAsync(
                content.Content,
                cancellationToken);
            return new(manifestExists, markerExists, manifest);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogControlMetadataOperation(
                "Ignored invalid or unreadable; full comparison will replace",
                ArchiveLogicalStateManifest.FileName);
            return new(manifestExists, markerExists, Manifest: null);
        }
    }

    internal async Task<ArchiveLogicalStateManifest> CreateLogicalStateManifestAsync
    (
        IObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        RestorePlan plan,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateLogicalStateManifestAsync));

        return await RestoreManifestEvidenceCollector.CreateAsync
        (
            destinationStore,
            destinationLayout,
            plan,
            _logicalStateManifestSerializer,
            _logger,
            cancellationToken
        );
    }

    internal async Task UploadLogicalStateManifestAsync
    (
        IObjectStore destinationStore,
        ArchiveLogicalStateManifest manifest,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(UploadLogicalStateManifestAsync));

        await using var content = _temporaryContentFactory.Create();
        await _logicalStateManifestSerializer.WriteAsync(
            manifest,
            content,
            cancellationToken);
        await content.SealAsync(cancellationToken);
        await using var reader = content.OpenRead();
        await destinationStore.UploadAsync(
            ArchiveLogicalStateManifest.FileName,
            reader,
            "application/json",
            EmptyMetadata,
            cancellationToken);
        _logger.LogControlMetadataOperation(
            "Wrote",
            ArchiveLogicalStateManifest.FileName);
    }

    internal async Task UploadLogicalStateManifestInvalidationMarkerAsync
    (
        IObjectStore destinationStore,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(UploadLogicalStateManifestInvalidationMarkerAsync));

        await using var markerContent = new MemoryStream(
            LogicalStateManifestInvalidationMarkerContent,
            writable: false);
        await destinationStore.UploadAsync(
            ArchiveLogicalStateManifest.InvalidationMarkerFileName,
            markerContent,
            "application/json",
            EmptyMetadata,
            cancellationToken);
        _logger.LogControlMetadataOperation(
            "Wrote",
            ArchiveLogicalStateManifest.InvalidationMarkerFileName);
    }

    internal async Task<ChangeManifestLoad> ReadChangeManifestAsync
    (
        IObjectStore targetStore,
        bool recoverInvalidManifest,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReadChangeManifestAsync));

        var existingFileNames = new List<string>(2);
        foreach (var fileName in GetChangeManifestFileNames())
        {
            if (await targetStore.ExistsAsync(fileName, cancellationToken))
            {
                existingFileNames.Add(fileName);
            }
        }

        var invalidationMarkerExists = await targetStore.ExistsAsync(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            cancellationToken);
        if (existingFileNames.Count == 0 && !invalidationMarkerExists)
        {
            return new(existingFileNames, false, null);
        }

        ArchiveChangeManifest? selectedManifest = null;
        try
        {
            if (invalidationMarkerExists)
            {
                throw new InvalidDataException(
                    "A prior change-manifest replacement did not complete.");
            }

            foreach (var fileName in existingFileNames)
            {
                var manifest = await ReadChangeManifestFileAsync(
                    targetStore,
                    fileName,
                    cancellationToken);
                if (selectedManifest is null)
                {
                    selectedManifest = manifest;
                }
                else
                {
                    using (manifest)
                    {
                        if (!string.Equals(
                                selectedManifest.ManifestHash,
                                manifest.ManifestHash,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "The compressed and uncompressed change manifests describe different live states.");
                        }
                    }
                }
            }

            return new(existingFileNames, false, selectedManifest);
        }
        catch (Exception ex)
        {
            selectedManifest?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            var evidenceFileNames = invalidationMarkerExists ?
                existingFileNames.Append(ArchiveChangeManifest.InvalidationMarkerFileName) :
                existingFileNames;
            var manifestNames = string.Join("', '", evidenceFileNames);
            if (recoverInvalidManifest)
            {
                _logger.LogInvalidChangeManifestIgnored(
                    manifestNames,
                    ex);
                return new(existingFileNames, invalidationMarkerExists, null);
            }

            throw new YabtSyncException(
                $"Change manifest state containing '{manifestNames}' could not be read or validated.",
                ex);
        }
    }

    internal async Task<ArchiveChangeManifest> ReadChangeManifestFileAsync
    (
        IObjectStore targetStore,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReadChangeManifestFileAsync));

        _logger.LogControlMetadataOperation("Reading", fileName);
        await using var content = await targetStore.OpenReadAsync(
            fileName,
            cancellationToken);
        if (!string.Equals(
                fileName,
                ArchiveChangeManifest.BrotliFileName,
                StringComparison.Ordinal))
        {
            return await _changeManifestSerializer.ReadAsync(
                content.Content,
                cancellationToken);
        }

        await using var decompressedContent = new BrotliStream(
            content.Content,
            CompressionMode.Decompress,
            leaveOpen: true);
        return await _changeManifestSerializer.ReadAsync(
            decompressedContent,
            cancellationToken);
    }

    internal async Task UploadChangeManifestAsync
    (
        IObjectStore targetStore,
        ArchiveChangeManifest manifest,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(UploadChangeManifestAsync));

        try
        {
            await using var content = _temporaryContentFactory.Create();
            if (string.Equals(
                    fileName,
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal))
            {
                await using (var compressedContent = new BrotliStream(
                    content,
                    CompressionLevel.Optimal,
                    leaveOpen: true))
                {
                    await _changeManifestSerializer.WriteAsync(
                        manifest,
                        compressedContent,
                        cancellationToken);
                }
            }
            else
            {
                await _changeManifestSerializer.WriteAsync(
                    manifest,
                    content,
                    cancellationToken);
            }

            await content.SealAsync(cancellationToken);
            await using var reader = content.OpenRead();

            await targetStore.UploadAsync(
                fileName,
                reader,
                string.Equals(
                    fileName,
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal) ?
                        "application/octet-stream" :
                        "application/json",
                EmptyMetadata,
                cancellationToken);
            _logger.LogControlMetadataOperation("Wrote", fileName);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Change manifest '{fileName}' could not be uploaded.",
                ex);
        }
    }

    internal async Task UploadChangeManifestInvalidationMarkerAsync
    (
        IObjectStore targetStore,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(UploadChangeManifestInvalidationMarkerAsync));

        try
        {
            await using var content = new MemoryStream([], writable: false);
            await targetStore.UploadAsync(
                ArchiveChangeManifest.InvalidationMarkerFileName,
                content,
                "application/octet-stream",
                EmptyMetadata,
                cancellationToken);
            _logger.LogControlMetadataOperation(
                "Wrote",
                ArchiveChangeManifest.InvalidationMarkerFileName);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Change manifest invalidation marker " +
                    $"'{ArchiveChangeManifest.InvalidationMarkerFileName}' could not be uploaded.",
                ex);
        }
    }

    internal async Task DeleteChangeManifestsAsync
    (
        IArchiveMutableObjectStore targetStore,
        IEnumerable<string> fileNames,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(DeleteChangeManifestsAsync));

        foreach (var fileName in fileNames)
        {
            await DeleteInternalObjectAsync(
                targetStore,
                fileName,
                $"Obsolete change manifest '{fileName}'",
                cancellationToken);
        }
    }

    internal async Task DeleteInternalObjectAsync
    (
        IArchiveMutableObjectStore targetStore,
        string key,
        string description,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(DeleteInternalObjectAsync));

        try
        {
            if (!await targetStore.ExistsAsync(key, cancellationToken))
            {
                return;
            }

            _logger.LogControlMetadataOperation(
                "Reading for guarded deletion",
                key);
            var expectedContentHash = await ArchiveContentTransfer.ComputeStoredObjectHashAsync(
                targetStore,
                key,
                cancellationToken);
            var deleted = await targetStore.TryDeleteIfContentHashMatchesAsync(
                key,
                expectedContentHash,
                cancellationToken);
            if (!deleted)
            {
                throw new YabtSyncException(
                    $"{description} changed before it could be deleted.");
            }

            _logger.LogControlMetadataOperation("Deleted", key);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{description} at '{key}' could not be deleted.",
                ex);
        }
    }

    private static IEnumerable<string> GetChangeManifestFileNames() =>
    [
        ArchiveChangeManifest.BrotliFileName,
        ArchiveChangeManifest.UncompressedFileName,
    ];

    internal static string GetChangeManifestFileName(string? configuredCompression)
    {
        var effectiveCompression = ArchiveChangeManifestCompression.GetEffective(
            configuredCompression);
        return effectiveCompression switch
        {
            ArchiveChangeManifestCompression.Brotli => ArchiveChangeManifest.BrotliFileName,
            ArchiveChangeManifestCompression.None => ArchiveChangeManifest.UncompressedFileName,
            _ => throw new YabtSyncException(
                $"Unsupported change manifest compression '{effectiveCompression}'."),
        };
    }
}
