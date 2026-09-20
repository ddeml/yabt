using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipArchiveFormatHandler
(
    ILogger<ZipArchiveFormatHandler> _logger,
    IOptionsMonitor<ZipArchiveFormatOptions> _options,
    IManifestSerializer _manifestSerializer
) : IArchiveFormatHandler
{
    private const int DefaultHashBufferSize = 81_920;
    private const string EmptyFolderMarkerFingerprint = "yabt-empty-v1:present";
    private const int MaximumManifestLength = 16 * 1024 * 1024;

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

    private static readonly DateTimeOffset MinimumZipLastModifiedUtc = new
    (
        1980,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero
    );

    private static readonly DateTimeOffset MaximumZipLastModifiedUtc = new
    (
        2107,
        12,
        31,
        23,
        59,
        58,
        TimeSpan.Zero
    );

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    public string FormatName => ZipArchiveFormatName.Value;

    public bool ProjectsBesideSourceFolder => true;

    public bool CanRestoreArtifact(ArchiveProjectedObject artifact)
    {
        _logger.LogTrace(nameof(CanRestoreArtifact));

        ArgumentNullException.ThrowIfNull(artifact);
        var changeFingerprint = artifact.ChangeFingerprint;
        if (changeFingerprint is null ||
            !ArchiveHash.IsValid(changeFingerprint) ||
            !TryParsePackagePath(
                artifact.RelativePath,
                out _,
                out var packageFingerprintToken))
        {
            return false;
        }

        return string.Equals(
            packageFingerprintToken,
            ArchiveHash.FormatFileNameToken(changeFingerprint),
            StringComparison.Ordinal);
    }

    public async IAsyncEnumerable<ArchiveProjectedObject> ProjectBackupAsync
    (
        ArchiveProjectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectBackupAsync));

        ArgumentNullException.ThrowIfNull(request);

        await request.SourceStore.EnsureReadyAsync(cancellationToken);

        var sourceObjects = await ListSourceObjectsAsync(
            request,
            cancellationToken);
        var compressionLevel = _options.CurrentValue.CompressionLevel ?? default;
        ZipPackageBuildResult? prebuiltPackage = null;
        if (sourceObjects.Any(sourceObject => sourceObject.ChangeFingerprint is null))
        {
            // A deterministic package name needs the missing content fingerprints. Build the ZIP
            // while calculating them so no source object must be opened once for naming and again
            // for packaging.
            prebuiltPackage = await BuildPackageEntriesAsync
            (
                request.SourceStore,
                sourceObjects,
                compressionLevel,
                cancellationToken
            );
            sourceObjects = prebuiltPackage.SourceObjects;
        }

        var policy = request.Policy ?? new FolderPolicy(ZipArchiveFormatName.Value);
        var sourcePath = ArchiveLayout.NormalizeObjectKey(request.LogicalPath);
        var packageFingerprint = ComputePackageFingerprint(
            sourceObjects,
            compressionLevel,
            sourcePath,
            policy);
        var packageName = CreatePackageName(
            request.SourceDisplayName,
            request.SourcePrefix,
            packageFingerprint.FileNameToken);
        var packageCreationTimeUtc = GetPackageCreationTimeUtc(sourceObjects);
        var materialization = prebuiltPackage is null ?
            new ZipPackageProjectionMaterialization
            (
                cancellationToken => BuildMaterializedPackageAsync
                (
                    request.SourceStore,
                    sourceObjects,
                    compressionLevel,
                    sourcePath,
                    packageCreationTimeUtc,
                    packageFingerprint.ChangeFingerprint,
                    packageName,
                    policy,
                    cancellationToken
                )
            ) :
            new ZipPackageProjectionMaterialization
            (
                await CompleteMaterializedPackageAsync
                (
                    prebuiltPackage,
                    compressionLevel,
                    sourcePath,
                    packageCreationTimeUtc,
                    packageFingerprint.ChangeFingerprint,
                    packageName,
                    policy,
                    cancellationToken
                )
            );
        var projection = new ArchiveProjectionProvenance
        (
            sourcePath,
            ZipArchiveFormatName.Value,
            ZipArchiveFormatVersion.Value,
            packageFingerprint.ChangeFingerprint,
            ArchiveProjectionArtifactRoles.Package
        );

        yield return CreatePackageObject
        (
            packageName,
            packageCreationTimeUtc,
            packageFingerprint.ChangeFingerprint,
            projection,
            materialization
        );

        yield return CreateManifestObject
        (
            packageName,
            packageCreationTimeUtc,
            packageFingerprint.ChangeFingerprint,
            projection with
            {
                ArtifactRole = ArchiveProjectionArtifactRoles.Manifest,
            },
            materialization
        );
    }

    public async Task<ArchiveRestoreProjection> ProjectRestoreAsync
    (
        ArchiveRestoreRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectRestoreAsync));

        ArgumentNullException.ThrowIfNull(request);

        if (request.RequireCompleteProjection || request.Artifacts.Count > 1)
        {
            return await ProjectManifestRestoreAsync(
                request,
                cancellationToken);
        }

        var artifact = request.Artifact;
        if (!CanRestoreArtifact(artifact) ||
            !TryParsePackagePath(
                artifact.RelativePath,
                out var packageFolderName,
                out _))
        {
            throw new YabtFormatZipException(
                $"Archive artifact '{artifact.RelativePath}' is not a recognized YABT ZIP package.");
        }

        if (!ArchiveHash.IsValid(artifact.ContentHash))
        {
            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' has no valid content hash.");
        }

        var packageOutputPrefix = request.RestoreAsRoot ?
            string.Empty :
            ArchiveLayout.CombinePrefixAndRelativePath(
                GetParentPrefix(artifact.RelativePath),
                packageFolderName);
        var stagedPackage = await StageRestorePackageAsync(
            artifact,
            cancellationToken);
        var retainStagedPackage = false;
        try
        {
            var files = new List<ArchiveProjectedObject>();
            var filePaths = new HashSet<string>(StringComparer.Ordinal);
            var directoryPaths = new HashSet<string>(StringComparer.Ordinal);

            await using var packageContent = stagedPackage.OpenPackage();
            using var archive = new ZipArchive(
                packageContent,
                ZipArchiveMode.Read,
                leaveOpen: true);
            ZipArchiveEntry? embeddedManifestEntry = null;
            foreach (var entry in archive.Entries)
            {
                ValidateZipEntry(entry, artifact.RelativePath);
                _ = NormalizeZipEntryPath(entry.FullName);
                if (!string.Equals(
                        entry.FullName,
                        ArchivePackageManifestFileNames.EmbeddedEntryName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (embeddedManifestEntry is not null)
                {
                    throw new YabtFormatZipException(
                        $"ZIP package '{artifact.RelativePath}' has multiple embedded package manifests.");
                }

                embeddedManifestEntry = entry;
            }

            if (embeddedManifestEntry is null)
            {
                throw new YabtFormatZipException(
                    $"ZIP package '{artifact.RelativePath}' has no embedded package manifest.");
            }
            ArchiveManifest manifest;
            _logger.LogZipEmbeddedManifestRead(artifact.RelativePath);
            await using (var manifestContent = embeddedManifestEntry.Open())
            using (var boundedManifestContent = new MemoryStream(
                await ReadBoundedManifestContentAsync(
                    manifestContent,
                    $"ZIP package '{artifact.RelativePath}' embedded manifest",
                    cancellationToken),
                writable: false))
            {
                manifest = await _manifestSerializer.ReadAsync(
                    boundedManifestContent,
                    cancellationToken);
            }

            ValidateRestoreManifest(
                manifest,
                artifact);
            var payloadEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceEquals(entry, embeddedManifestEntry)) { continue; }

                ValidateZipEntry(entry, artifact.RelativePath);
                var entryRelativePath = NormalizeZipEntryPath(entry.FullName);
                if (string.IsNullOrEmpty(entry.Name) ||
                    !string.Equals(
                        entryRelativePath,
                        entry.FullName,
                        StringComparison.Ordinal) ||
                    !payloadEntries.TryAdd(entryRelativePath, entry))
                {
                    throw new YabtFormatZipException(
                        $"ZIP package '{artifact.RelativePath}' contains unsupported or duplicate " +
                        $"entry '{entry.FullName}'.");
                }
            }

            foreach (var manifestEntry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!payloadEntries.Remove(
                        manifestEntry.StoredPath,
                        out var entry))
                {
                    throw new YabtFormatZipException(
                        $"ZIP package '{artifact.RelativePath}' is missing manifest entry " +
                        $"'{manifestEntry.StoredPath}'.");
                }

                if (entry.Length != manifestEntry.Length)
                {
                    throw new YabtFormatZipException(
                        $"ZIP package '{artifact.RelativePath}' entry '{entry.FullName}' has length " +
                        $"{entry.Length}, but the embedded manifest records {manifestEntry.Length}.");
                }

                var restorePath = ArchiveLayout.CombinePrefixAndRelativePath(
                    packageOutputPrefix,
                    manifestEntry.RelativePath);
                if (string.Equals(
                        manifestEntry.Kind,
                        ArchiveManifestEntryKinds.Directory,
                        StringComparison.Ordinal))
                {
                    AddRestoreDirectory(
                        restorePath,
                        filePaths,
                        directoryPaths);
                    continue;
                }

                AddRestoreFilePath(
                    restorePath,
                    filePaths,
                    directoryPaths);
                var entryName = entry.FullName;
                var projection = manifestEntry.Projection is null ?
                    null :
                    manifestEntry.Projection with
                    {
                        LogicalPath = ArchiveLayout.CombinePrefixAndRelativePath(
                            packageOutputPrefix,
                            manifestEntry.Projection.LogicalPath),
                    };
                files.Add(new
                (
                    restorePath,
                    currentCancellationToken => stagedPackage.OpenEntryAsync(
                        entryName,
                        currentCancellationToken),
                    manifestEntry.Length,
                    manifestEntry.LastModifiedUtc,
                    manifestEntry.ContentHash,
                    GetRestoreChangeFingerprint(manifestEntry),
                    projection
                ));
            }

            if (payloadEntries.Count != 0)
            {
                var unexpectedPath = payloadEntries.Keys.Min(StringComparer.Ordinal);
                throw new YabtFormatZipException(
                    $"ZIP package '{artifact.RelativePath}' contains entry '{unexpectedPath}' " +
                    "that is absent from its embedded manifest.");
            }

            var result = new ArchiveRestoreProjection(
                files,
                directoryPaths.Order(StringComparer.Ordinal),
                stagedPackage);
            retainStagedPackage = true;
            return result;
        }
        catch (Exception ex)
        {
            if (ex is YabtFormatZipException) { throw; }

            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' could not be safely prepared for restore.",
                ex);
        }
        finally
        {
            if (!retainStagedPackage)
            {
                await stagedPackage.DisposeAsync();
            }
        }
    }

    private async Task<StagedZipRestorePackage> StageRestorePackageAsync
    (
        ArchiveProjectedObject artifact,
        CancellationToken cancellationToken
    )
    {
        var temporaryPath = Path.Combine
        (
            Path.GetTempPath(),
            $"yabt-zip-restore-{Guid.NewGuid():N}.tmp"
        );
        var retainTemporaryPath = false;
        try
        {
            _logger.LogZipTemporaryPlumbingOperation(
                "Creating restore staging file",
                temporaryPath);
            await using var stagedContent = CreateRestoreStagingFileStream(temporaryPath);
            var hash = new XxHash128();
            long contentLength = 0;
            _logger.LogZipRestoreArtifactRead(artifact.RelativePath);
            await using var packageContent = await artifact.OpenContentAsync(cancellationToken);
            var buffer = new byte[DefaultHashBufferSize];
            while (true)
            {
                var bytesRead = await packageContent.Content.ReadAsync(
                    buffer,
                    cancellationToken);
                if (bytesRead == 0) { break; }

                hash.Append(buffer.AsSpan(0, bytesRead));
                contentLength += bytesRead;
                await stagedContent.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
            }

            await stagedContent.FlushAsync(cancellationToken);
            _logger.LogZipTemporaryPlumbingOperation(
                "Finished writing restore staging file",
                temporaryPath);
            if (artifact.ContentLength.HasValue &&
                artifact.ContentLength.Value != contentLength)
            {
                throw new YabtFormatZipException(
                    $"ZIP package '{artifact.RelativePath}' has length {contentLength}, " +
                        $"but {artifact.ContentLength.Value} bytes were expected.");
            }

            var actualHash = ArchiveHash.Format(hash.GetHashAndReset());
            if (!string.Equals(
                    artifact.ContentHash,
                    actualHash,
                    StringComparison.Ordinal))
            {
                throw new YabtFormatZipException(
                    $"ZIP package '{artifact.RelativePath}' failed its content hash check.");
            }

            var stagedPackage = new StagedZipRestorePackage(
                temporaryPath,
                artifact.RelativePath,
                _logger);
            retainTemporaryPath = true;
            return stagedPackage;
        }
        catch (Exception ex)
        {
            if (ex is YabtFormatZipException) { throw; }

            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' could not be staged for restore.",
                ex);
        }
        finally
        {
            if (!retainTemporaryPath)
            {
                TryDeleteRestoreTemporaryPath(temporaryPath);
            }
        }
    }

    private static FileStream CreateRestoreStagingFileStream(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = DefaultHashBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new(path, options);
    }

    private void TryDeleteRestoreTemporaryPath(string path)
    {
        _logger.LogTrace(nameof(TryDeleteRestoreTemporaryPath));
        _logger.LogZipTemporaryPlumbingOperation(
            "Deleting restore staging file",
            path);

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogIgnoringZipRestoreTemporaryPathDeleteException(ex, path);
        }
    }

    private static void ValidateZipEntry
    (
        ZipArchiveEntry entry,
        string packageRelativePath
    )
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType == 0xA000 ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new YabtFormatZipException(
                $"ZIP package '{packageRelativePath}' contains linked entry '{entry.FullName}'.");
        }

        if (string.IsNullOrEmpty(entry.FullName) ||
            entry.FullName.StartsWith('/') ||
            entry.FullName.StartsWith('\\') ||
            entry.FullName.Contains('\\'))
        {
            throw new YabtFormatZipException(
                $"ZIP package '{packageRelativePath}' contains unsafe entry path '{entry.FullName}'.");
        }
    }

    private static void ValidateRestoreManifest
    (
        ArchiveManifest manifest,
        ArchiveProjectedObject artifact
    )
    {
        if (!string.Equals(
                manifest.Format,
                ZipArchiveFormatName.Value,
                StringComparison.Ordinal) ||
            manifest.FormatVersion != ZipArchiveFormatVersion.Value)
        {
            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' has unsupported format provenance.");
        }

        var packageFileName = Path.GetFileName(artifact.RelativePath);
        if (!string.Equals(
                manifest.PackageName,
                packageFileName,
                StringComparison.Ordinal))
        {
            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' does not match the package name in its manifest.");
        }

        if (!string.Equals(
                manifest.ProjectionId,
                artifact.ChangeFingerprint,
                StringComparison.Ordinal))
        {
            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' does not match the projection id in its manifest.");
        }

        if (!manifest.Entries.Any())
        {
            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' has an empty package manifest.");
        }

        if (artifact.Projection is null) { return; }

        if (!string.Equals(
                artifact.Projection.Format,
                ZipArchiveFormatName.Value,
                StringComparison.Ordinal) ||
            artifact.Projection.FormatVersion != ZipArchiveFormatVersion.Value ||
            !string.Equals(
                artifact.Projection.ProjectionId,
                manifest.ProjectionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.Projection.ArtifactRole,
                ArchiveProjectionArtifactRoles.Package,
                StringComparison.Ordinal))
        {
            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' has inconsistent projection provenance.");
        }
    }

    private static string GetRestoreChangeFingerprint(ArchiveManifestEntry manifestEntry) =>
        manifestEntry.Projection?.ProjectionId ??
        ArchiveChangeFingerprint.Create(
            manifestEntry.Length,
            manifestEntry.LastModifiedUtc);

    private static string NormalizeZipEntryPath(string entryPath)
    {
        var pathWithoutDirectoryMarker = entryPath.EndsWith('/') ?
            entryPath[..^1] :
            entryPath;
        try
        {
            var normalizedPath = ArchiveLayout.NormalizeObjectKey(pathWithoutDirectoryMarker);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                throw new YabtFormatZipException(
                    $"ZIP entry path '{entryPath}' does not identify a restore item.");
            }

            return normalizedPath;
        }
        catch (ArgumentException ex)
        {
            throw new YabtFormatZipException(
                $"ZIP entry path '{entryPath}' is unsafe.",
                ex);
        }
    }

    private static void AddRestoreFilePath
    (
        string relativePath,
        HashSet<string> filePaths,
        HashSet<string> directoryPaths
    )
    {
        if (!filePaths.Add(relativePath) || directoryPaths.Contains(relativePath))
        {
            throw new YabtFormatZipException(
                $"Multiple ZIP entries restore to the same path '{relativePath}'.");
        }

        var parentPath = GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(parentPath))
        {
            if (filePaths.Contains(parentPath))
            {
                throw new YabtFormatZipException(
                    $"ZIP restore file '{relativePath}' conflicts with file '{parentPath}'.");
            }

            directoryPaths.Add(parentPath);
            parentPath = GetParentPrefix(parentPath);
        }
    }

    private static void AddRestoreDirectory
    (
        string relativePath,
        HashSet<string> filePaths,
        HashSet<string> directoryPaths
    )
    {
        if (string.IsNullOrEmpty(relativePath)) { return; }

        if (filePaths.Contains(relativePath))
        {
            throw new YabtFormatZipException(
                $"ZIP restore directory '{relativePath}' conflicts with a file at the same path.");
        }

        directoryPaths.Add(relativePath);
        var parentPath = GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(parentPath))
        {
            if (filePaths.Contains(parentPath))
            {
                throw new YabtFormatZipException(
                    $"ZIP restore directory '{relativePath}' conflicts with file '{parentPath}'.");
            }

            directoryPaths.Add(parentPath);
            parentPath = GetParentPrefix(parentPath);
        }
    }

    private static string GetParentPrefix(string relativePath)
    {
        var normalizedPath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedPath.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalizedPath[..separator];
    }

    private static bool TryParsePackagePath
    (
        string relativePath,
        out string packageFolderName,
        out string packageFingerprintToken
    )
    {
        packageFolderName = string.Empty;
        packageFingerprintToken = string.Empty;
        var fileName = Path.GetFileName(relativePath);
        var tokenSeparator = $".{ArchiveHash.AlgorithmName}-";
        var tokenIndex = fileName.LastIndexOf(tokenSeparator, StringComparison.Ordinal);
        if (tokenIndex <= 0 ||
            !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokenStart = tokenIndex + tokenSeparator.Length;
        var tokenLength = fileName.Length - tokenStart - ".zip".Length;
        if (tokenLength != ArchiveHash.FileNameEncodedValueLength)
        {
            return false;
        }

        var token = fileName.AsSpan(tokenStart, tokenLength);
        foreach (var character in token)
        {
            if (character is < '0' or > '9' and < 'a' or > 'v')
            {
                return false;
            }
        }

        if (token[^1] is not ('0' or '4' or '8' or 'c' or 'g' or 'k' or 'o' or 's'))
        {
            return false;
        }

        packageFolderName = fileName[..tokenIndex];
        packageFingerprintToken = fileName[
            (tokenIndex + 1)..(fileName.Length - ".zip".Length)];
        return true;
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
        var hasItems = false;
        var sourceItems = sourceStore.GetFolderItemsAsync(
            folderPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var sourceItem in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasItems = true;

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
                fingerprintResult.ChangeFingerprint,
                sourceItem.Object.ContentHash,
                sourceItem.Object.Projection,
                IsEmptyFolderMarker: false
            ));
        }

        if (hasItems) { return; }

        var folderRelativePath = ArchiveLayout.RemovePrefix(
            ArchiveLayout.NormalizeObjectKey(folderPrefix),
            sourcePrefix);
        var markerRelativePath = ArchiveLayout.CombinePrefixAndRelativePath(
            folderRelativePath,
            ArchiveFolderMarkerFileNames.EmptyFolder);
        sourceObjects.Add(new
        (
            SourceKey: null,
            markerRelativePath,
            Length: 0,
            DefaultLastModifiedUtc,
            EmptyFolderMarkerFingerprint,
            ContentHash: ArchiveHash.Compute([]),
            Projection: null,
            IsEmptyFolderMarker: true
        ));
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

    private async Task<ZipPackageBuildResult> BuildPackageEntriesAsync
    (
        IReadOnlyObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
        CompressionLevel compressionLevel,
        CancellationToken cancellationToken
    )
    {
        var package = new MemoryStream();
        var completedSourceObjects = new List<ZipSourceObject>(sourceObjects.Count);
        var sourceReadFailures = new List<Exception>();
        byte[]? hashBuffer = null;
        try
        {
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var sourceObject in sourceObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.Equals(
                            sourceObject.RelativePath,
                            ArchivePackageManifestFileNames.EmbeddedEntryName,
                            StringComparison.Ordinal))
                    {
                        throw new YabtFormatZipException(
                            $"ZIP source contains reserved package manifest path " +
                            $"'{ArchivePackageManifestFileNames.EmbeddedEntryName}'.");
                    }

                    if (sourceObject.IsEmptyFolderMarker)
                    {
                        var markerEntry = archive.CreateEntry
                        (
                            sourceObject.RelativePath,
                            compressionLevel
                        );
                        markerEntry.LastWriteTime = ToZipEntryLastModifiedUtc(
                            sourceObject.LastModifiedUtc);
                        await using var markerContent = markerEntry.Open();
                        completedSourceObjects.Add(sourceObject with
                        {
                            Length = 0,
                            ContentHash = ArchiveHash.Compute([]),
                        });
                        continue;
                    }

                    var sourceKey = sourceObject.SourceKey ??
                        throw new InvalidOperationException(
                            $"ZIP source object '{sourceObject.RelativePath}' has no source key.");
                    _logger.LogZipSourceObjectRead(sourceKey, sourceObject.RelativePath);
                    ArchiveObjectContent sourceContent;
                    try
                    {
                        sourceContent = await sourceStore.OpenReadAsync(
                            sourceKey,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        sourceReadFailures.Add(ex);
                        continue;
                    }

                    await using (sourceContent)
                    {
                        var entry = archive.CreateEntry
                        (
                            sourceObject.RelativePath,
                            compressionLevel
                        );
                        entry.LastWriteTime = ToZipEntryLastModifiedUtc(
                            sourceObject.LastModifiedUtc);
                        await using var entryContent = entry.Open();
                        var hash = new XxHash128();
                        hashBuffer ??= new byte[GetEffectiveHashBufferSize()];
                        long length = 0;
                        var sourceReadFailed = false;
                        while (true)
                        {
                            int bytesRead;
                            try
                            {
                                bytesRead = await sourceContent.Content.ReadAsync(
                                    hashBuffer,
                                    cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                sourceReadFailures.Add(ex);
                                sourceReadFailed = true;
                                break;
                            }

                            if (bytesRead == 0) { break; }

                            hash.Append(hashBuffer.AsSpan(0, bytesRead));
                            length += bytesRead;
                            await entryContent.WriteAsync(
                                hashBuffer.AsMemory(0, bytesRead),
                                cancellationToken);
                        }

                        if (sourceReadFailed) { continue; }

                        completedSourceObjects.Add(sourceObject with
                        {
                            Length = length,
                            ContentHash = ArchiveHash.Format(hash.GetHashAndReset()),
                        });
                    }
                }
            }

            if (sourceReadFailures.Count != 0)
            {
                throw new AggregateException(
                    $"ZIP package could not read {sourceReadFailures.Count} source object(s).",
                    sourceReadFailures);
            }

            for (var index = 0; index < completedSourceObjects.Count; index++)
            {
                var completedSourceObject = completedSourceObjects[index];
                if (completedSourceObject.ChangeFingerprint is null)
                {
                    completedSourceObjects[index] = completedSourceObject with
                    {
                        ChangeFingerprint = completedSourceObject.ContentHash,
                    };
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

    private async Task<ZipPackageMaterializedContent> BuildMaterializedPackageAsync
    (
        IReadOnlyObjectStore sourceStore,
        IReadOnlyList<ZipSourceObject> sourceObjects,
        CompressionLevel compressionLevel,
        string sourcePath,
        DateTimeOffset createdAtUtc,
        string projectionId,
        string packageName,
        FolderPolicy policy,
        CancellationToken cancellationToken
    )
    {
        var package = await BuildPackageEntriesAsync
        (
            sourceStore,
            sourceObjects,
            compressionLevel,
            cancellationToken
        );
        return await CompleteMaterializedPackageAsync
        (
            package,
            compressionLevel,
            sourcePath,
            createdAtUtc,
            projectionId,
            packageName,
            policy,
            cancellationToken
        );
    }

    private async Task<ZipPackageMaterializedContent> CompleteMaterializedPackageAsync
    (
        ZipPackageBuildResult package,
        CompressionLevel compressionLevel,
        string sourcePath,
        DateTimeOffset createdAtUtc,
        string projectionId,
        string packageName,
        FolderPolicy policy,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var manifestEntries = package.SourceObjects
                .Select(sourceObject => CreateManifestEntry(
                    sourceObject,
                    sourcePath))
                .ToArray();
            var manifest = _manifestSerializer.Create
            (
                sourcePath,
                createdAtUtc,
                ZipArchiveFormatName.Value,
                ZipArchiveFormatVersion.Value,
                projectionId,
                packageName,
                policy,
                manifestEntries
            );
            using var serializedManifest = new ManifestSizeLimitedMemoryStream
            (
                packageName,
                MaximumManifestLength
            );
            try
            {
                await _manifestSerializer.WriteAsync(
                    manifest,
                    serializedManifest,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                if (!serializedManifest.LimitExceeded) { throw; }

                throw new YabtFormatZipException(
                    $"ZIP package manifest for '{packageName}' exceeds the " +
                        $"{MaximumManifestLength} byte restore safety limit.",
                    ex);
            }
            var manifestBytes = serializedManifest.ToArray();
            if (manifestBytes.LongLength > MaximumManifestLength)
            {
                throw new YabtFormatZipException(
                    $"ZIP package manifest for '{packageName}' is {manifestBytes.LongLength} " +
                        $"bytes, which exceeds the {MaximumManifestLength} byte restore safety limit.");
            }

            package.Content.Position = 0;
            using (var archive = new ZipArchive(
                package.Content,
                ZipArchiveMode.Update,
                leaveOpen: true))
            {
                if (archive.GetEntry(ArchivePackageManifestFileNames.EmbeddedEntryName) is not null)
                {
                    throw new YabtFormatZipException(
                        $"ZIP source contains reserved package manifest path " +
                        $"'{ArchivePackageManifestFileNames.EmbeddedEntryName}'.");
                }

                var manifestEntry = archive.CreateEntry
                (
                    ArchivePackageManifestFileNames.EmbeddedEntryName,
                    compressionLevel
                );
                manifestEntry.LastWriteTime = ToZipEntryLastModifiedUtc(createdAtUtc);
                await using var manifestEntryContent = manifestEntry.Open();
                await manifestEntryContent.WriteAsync(
                    manifestBytes,
                    cancellationToken);
            }

            return new
            (
                package.Content.ToArray(),
                manifestBytes,
                manifest
            );
        }
        finally
        {
            await package.Content.DisposeAsync();
        }
    }

    private static ArchiveManifestEntry CreateManifestEntry
    (
        ZipSourceObject sourceObject,
        string sourcePath
    )
    {
        var contentHash = sourceObject.ContentHash ??
            throw new InvalidOperationException(
                $"ZIP source object '{sourceObject.RelativePath}' has no content hash.");
        if (sourceObject.IsEmptyFolderMarker)
        {
            return new
            (
                ArchiveManifestEntryKinds.Directory,
                GetParentPrefix(sourceObject.RelativePath),
                sourceObject.RelativePath,
                0,
                sourceObject.LastModifiedUtc,
                contentHash
            );
        }

        var projection = sourceObject.Projection is null ?
            null :
            sourceObject.Projection with
            {
                LogicalPath = ArchiveLayout.RemovePrefix(
                    sourceObject.Projection.LogicalPath,
                    sourcePath),
            };
        return new
        (
            projection is null ?
                ArchiveManifestEntryKinds.File :
                ArchiveManifestEntryKinds.FormatArtifact,
            sourceObject.RelativePath,
            sourceObject.RelativePath,
            sourceObject.Length ??
                throw new InvalidOperationException(
                    $"ZIP source object '{sourceObject.RelativePath}' has no content length."),
            sourceObject.LastModifiedUtc,
            contentHash,
            projection
        );
    }

    private ArchiveProjectedObject CreatePackageObject
    (
        string packageName,
        DateTimeOffset lastModifiedUtc,
        string packageChangeFingerprint,
        ArchiveProjectionProvenance projection,
        ZipPackageProjectionMaterialization materialization
    )
    {
        async Task<ArchiveObjectContent> OpenPackageAsync(CancellationToken cancellationToken)
        {
            _logger.LogZipProjectedArtifactRead(
                ArchiveProjectionArtifactRoles.Package,
                packageName);
            var package = await materialization.GetAsync(cancellationToken);
            return new
            (
                new MemoryStream(package.PackageBytes, writable: false),
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
            ContentLength: materialization.PackageLength,
            LastModifiedUtc: lastModifiedUtc,
            ContentHash: null,
            ChangeFingerprint: packageChangeFingerprint,
            Projection: projection
        );
    }

    private ArchiveProjectedObject CreateManifestObject
    (
        string packageName,
        DateTimeOffset lastModifiedUtc,
        string packageChangeFingerprint,
        ArchiveProjectionProvenance projection,
        ZipPackageProjectionMaterialization materialization
    )
    {
        async Task<ArchiveObjectContent> OpenManifestAsync(CancellationToken cancellationToken)
        {
            var manifestPath = $"{packageName}{ArchivePackageManifestFileNames.AdjacentSuffix}";
            _logger.LogZipProjectedArtifactRead(
                ArchiveProjectionArtifactRoles.Manifest,
                manifestPath);
            var package = await materialization.GetAsync(cancellationToken);
            return new
            (
                new MemoryStream(package.ManifestBytes, writable: false),
                "application/json",
                EmptyMetadata
            );
        }

        return new
        (
            $"{packageName}{ArchivePackageManifestFileNames.AdjacentSuffix}",
            OpenManifestAsync,
            ContentLength: materialization.ManifestLength,
            LastModifiedUtc: lastModifiedUtc,
            ContentHash: null,
            ChangeFingerprint: CreateManifestChangeFingerprint(packageChangeFingerprint),
            Projection: projection
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
        CompressionLevel compressionLevel,
        string sourcePath,
        FolderPolicy policy
    )
    {
        var hash = new XxHash128();
        hash.Append(PackageFingerprintDomain);

        Span<byte> compressionLevelValue = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(
            compressionLevelValue,
            (int)compressionLevel);
        hash.Append(compressionLevelValue);
        AppendCanonicalString(hash, sourcePath);
        AppendCanonicalPolicy(hash, policy);

        foreach (var sourceObject in sourceObjects)
        {
            AppendCanonicalString(hash, sourceObject.RelativePath);
            AppendCanonicalNullableInt64(hash, sourceObject.Length);
            AppendCanonicalInt64(hash, sourceObject.LastModifiedUtc.UtcDateTime.Ticks);
            AppendCanonicalString(
                hash,
                sourceObject.ChangeFingerprint ??
                    throw new InvalidOperationException(
                        $"ZIP source object '{sourceObject.RelativePath}' has no change fingerprint."));
            AppendCanonicalProjection(hash, sourceObject.Projection);
        }

        var hashValue = hash.GetHashAndReset();
        return new
        (
            ArchiveHash.Format(hashValue),
            ArchiveHash.FormatFileNameToken(hashValue)
        );
    }

    private static void AppendCanonicalPolicy(XxHash128 hash, FolderPolicy policy)
    {
        AppendCanonicalString(hash, policy.Format);
        AppendCanonicalStrings(hash, policy.IncludePatterns);
        AppendCanonicalStrings(hash, policy.ExcludePatterns);
        if (policy.Options is null)
        {
            hash.Append([0]);
            return;
        }

        hash.Append([1]);
        var options = JsonSerializer.SerializeToElement(policy.Options);
        AppendCanonicalJsonValue(hash, options);
    }

    private static void AppendCanonicalStrings
    (
        XxHash128 hash,
        IEnumerable<string>? values
    )
    {
        if (values is null)
        {
            AppendCanonicalInt64(hash, -1);
            return;
        }

        var snapshot = values.ToArray();
        AppendCanonicalInt64(hash, snapshot.LongLength);
        foreach (var value in snapshot)
        {
            AppendCanonicalString(hash, value);
        }
    }

    private async Task<ArchiveRestoreProjection> ProjectManifestRestoreAsync
    (
        ArchiveRestoreRequest request,
        CancellationToken cancellationToken
    )
    {
        var packageArtifacts = request.Artifacts
            .Where(artifact => string.Equals(
                artifact.Projection?.ArtifactRole,
                ArchiveProjectionArtifactRoles.Package,
                StringComparison.Ordinal))
            .ToArray();
        var manifestArtifacts = request.Artifacts
            .Where(artifact => string.Equals(
                artifact.Projection?.ArtifactRole,
                ArchiveProjectionArtifactRoles.Manifest,
                StringComparison.Ordinal))
            .ToArray();
        if (request.Artifacts.Count != 2 ||
            packageArtifacts.Length != 1 ||
            manifestArtifacts.Length != 1)
        {
            throw new YabtFormatZipException(
                "A ZIP restore projection requires exactly one package artifact and one " +
                    "adjacent manifest artifact.");
        }

        var packageArtifact = packageArtifacts[0];
        var manifestArtifact = manifestArtifacts[0];
        if (!CanRestoreArtifact(packageArtifact) ||
            !ArchiveHash.IsValid(packageArtifact.ContentHash))
        {
            throw new YabtFormatZipException(
                $"ZIP package '{packageArtifact.RelativePath}' does not have valid restore evidence.");
        }

        ValidateProjectionPair(packageArtifact, manifestArtifact);
        _logger.LogZipAdjacentManifestRead(manifestArtifact.RelativePath);
        var manifestBytes = await ReadAndValidateManifestArtifactAsync(
            manifestArtifact,
            cancellationToken);
        ArchiveManifest manifest;
        using (var manifestContent = new MemoryStream(manifestBytes, writable: false))
        {
            manifest = await _manifestSerializer.ReadAsync(
                manifestContent,
                cancellationToken);
        }

        ValidateRestoreManifest(manifest, packageArtifact);
        var packageProjection = packageArtifact.Projection ??
            throw new YabtFormatZipException(
                $"ZIP package '{packageArtifact.RelativePath}' has no projection provenance.");
        if (!string.Equals(
                manifest.SourcePath,
                packageProjection.LogicalPath,
                StringComparison.Ordinal))
        {
            throw new YabtFormatZipException(
                $"ZIP package '{packageArtifact.RelativePath}' has a logical path that differs " +
                    "from its adjacent manifest.");
        }

        var materialization = new ZipRestoreProjectionMaterialization
        (
            cancellationToken => StageAndValidateRestorePackageAsync(
                packageArtifact,
                manifest,
                manifestBytes,
                cancellationToken)
        );
        var retainMaterialization = false;
        try
        {
            var files = new List<ArchiveProjectedObject>();
            var filePaths = new HashSet<string>(StringComparer.Ordinal);
            var directoryPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manifestEntry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var restorePath = ArchiveLayout.CombinePrefixAndRelativePath(
                    manifest.SourcePath,
                    manifestEntry.RelativePath);
                if (string.Equals(
                        manifestEntry.Kind,
                        ArchiveManifestEntryKinds.Directory,
                        StringComparison.Ordinal))
                {
                    AddRestoreDirectory(
                        restorePath,
                        filePaths,
                        directoryPaths);
                    continue;
                }

                AddRestoreFilePath(
                    restorePath,
                    filePaths,
                    directoryPaths);
                var storedPath = manifestEntry.StoredPath;
                var projection = manifestEntry.Projection is null ?
                    null :
                    manifestEntry.Projection with
                    {
                        LogicalPath = ArchiveLayout.CombinePrefixAndRelativePath(
                            manifest.SourcePath,
                            manifestEntry.Projection.LogicalPath),
                    };
                files.Add(new
                (
                    restorePath,
                    currentCancellationToken => materialization.OpenEntryAsync(
                        storedPath,
                        currentCancellationToken),
                    manifestEntry.Length,
                    manifestEntry.LastModifiedUtc,
                    manifestEntry.ContentHash,
                    GetRestoreChangeFingerprint(manifestEntry),
                    projection
                ));
            }

            if (files.Count == 0)
            {
                await materialization.ValidateAsync(cancellationToken);
            }

            var result = new ArchiveRestoreProjection(
                files,
                directoryPaths.Order(StringComparer.Ordinal),
                materialization);
            retainMaterialization = true;
            return result;
        }
        finally
        {
            if (!retainMaterialization)
            {
                await materialization.DisposeAsync();
            }
        }
    }

    private static void ValidateProjectionPair
    (
        ArchiveProjectedObject packageArtifact,
        ArchiveProjectedObject manifestArtifact
    )
    {
        var packageProjection = packageArtifact.Projection ??
            throw new YabtFormatZipException("ZIP package projection provenance is required.");
        var manifestProjection = manifestArtifact.Projection ??
            throw new YabtFormatZipException("ZIP manifest projection provenance is required.");
        if (!string.Equals(
                packageProjection.LogicalPath,
                manifestProjection.LogicalPath,
                StringComparison.Ordinal) ||
            !string.Equals(
                packageProjection.Format,
                manifestProjection.Format,
                StringComparison.Ordinal) ||
            packageProjection.FormatVersion != manifestProjection.FormatVersion ||
            !string.Equals(
                packageProjection.ProjectionId,
                manifestProjection.ProjectionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifestArtifact.RelativePath,
                $"{packageArtifact.RelativePath}{ArchivePackageManifestFileNames.AdjacentSuffix}",
                StringComparison.Ordinal))
        {
            throw new YabtFormatZipException(
                "ZIP package and adjacent manifest projection provenance do not agree.");
        }
    }

    private static async Task<byte[]> ReadAndValidateManifestArtifactAsync
    (
        ArchiveProjectedObject manifestArtifact,
        CancellationToken cancellationToken
    )
    {
        if (!ArchiveHash.IsValid(manifestArtifact.ContentHash) ||
            manifestArtifact.ContentLength is < 0 or > MaximumManifestLength)
        {
            throw new YabtFormatZipException(
                $"Adjacent package manifest '{manifestArtifact.RelativePath}' does not have " +
                    "valid bounded content evidence.");
        }

        await using var artifactContent = await manifestArtifact.OpenContentAsync(cancellationToken);
        using var content = new MemoryStream();
        var hash = new XxHash128();
        var buffer = new byte[DefaultHashBufferSize];
        long contentLength = 0;
        while (true)
        {
            var bytesRead = await artifactContent.Content.ReadAsync(
                buffer,
                cancellationToken);
            if (bytesRead == 0) { break; }

            contentLength += bytesRead;
            if (contentLength > MaximumManifestLength)
            {
                throw new YabtFormatZipException(
                    $"Adjacent package manifest '{manifestArtifact.RelativePath}' exceeds the " +
                        $"{MaximumManifestLength} byte safety limit.");
            }

            hash.Append(buffer.AsSpan(0, bytesRead));
            await content.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        if (manifestArtifact.ContentLength.HasValue &&
            manifestArtifact.ContentLength.Value != contentLength ||
            !string.Equals(
                manifestArtifact.ContentHash,
                ArchiveHash.Format(hash.GetHashAndReset()),
                StringComparison.Ordinal))
        {
            throw new YabtFormatZipException(
                $"Adjacent package manifest '{manifestArtifact.RelativePath}' failed its " +
                    "content evidence check.");
        }

        return content.ToArray();
    }

    private static async Task<byte[]> ReadBoundedManifestContentAsync
    (
        Stream source,
        string description,
        CancellationToken cancellationToken
    )
    {
        using var content = new MemoryStream();
        var buffer = new byte[DefaultHashBufferSize];
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0) { break; }

            if (content.Length + bytesRead > MaximumManifestLength)
            {
                throw new YabtFormatZipException(
                    $"{description} exceeds the {MaximumManifestLength} byte safety limit.");
            }

            await content.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        return content.ToArray();
    }

    private async Task<StagedZipRestorePackage> StageAndValidateRestorePackageAsync
    (
        ArchiveProjectedObject packageArtifact,
        ArchiveManifest manifest,
        byte[] adjacentManifestBytes,
        CancellationToken cancellationToken
    )
    {
        var stagedPackage = await StageRestorePackageAsync(
            packageArtifact,
            cancellationToken);
        var retainStagedPackage = false;
        try
        {
            await using var packageContent = stagedPackage.OpenPackage();
            using var archive = new ZipArchive(
                packageContent,
                ZipArchiveMode.Read,
                leaveOpen: true);
            ZipArchiveEntry? embeddedManifestEntry = null;
            var payloadEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateZipEntry(entry, packageArtifact.RelativePath);
                if (string.Equals(
                        entry.FullName,
                        ArchivePackageManifestFileNames.EmbeddedEntryName,
                        StringComparison.Ordinal))
                {
                    if (embeddedManifestEntry is not null)
                    {
                        throw new YabtFormatZipException(
                            $"ZIP package '{packageArtifact.RelativePath}' has multiple embedded " +
                                "package manifests.");
                    }

                    embeddedManifestEntry = entry;
                    continue;
                }

                var entryRelativePath = NormalizeZipEntryPath(entry.FullName);
                if (string.IsNullOrEmpty(entry.Name) ||
                    !string.Equals(
                        entryRelativePath,
                        entry.FullName,
                        StringComparison.Ordinal) ||
                    !payloadEntries.TryAdd(entryRelativePath, entry))
                {
                    throw new YabtFormatZipException(
                        $"ZIP package '{packageArtifact.RelativePath}' contains unsupported or " +
                            $"duplicate entry '{entry.FullName}'.");
                }
            }

            if (embeddedManifestEntry is null)
            {
                throw new YabtFormatZipException(
                    $"ZIP package '{packageArtifact.RelativePath}' has no embedded package manifest.");
            }

            byte[] embeddedManifestBytes;
            _logger.LogZipEmbeddedManifestRead(packageArtifact.RelativePath);
            await using (var embeddedManifestContent = embeddedManifestEntry.Open())
            using (var buffer = new MemoryStream())
            {
                var readBuffer = new byte[DefaultHashBufferSize];
                while (true)
                {
                    var bytesRead = await embeddedManifestContent.ReadAsync(
                        readBuffer,
                        cancellationToken);
                    if (bytesRead == 0) { break; }

                    if (buffer.Length + bytesRead > MaximumManifestLength)
                    {
                        throw new YabtFormatZipException(
                            $"ZIP package '{packageArtifact.RelativePath}' embedded manifest " +
                                $"exceeds the {MaximumManifestLength} byte safety limit.");
                    }

                    await buffer.WriteAsync(
                        readBuffer.AsMemory(0, bytesRead),
                        cancellationToken);
                }

                embeddedManifestBytes = buffer.ToArray();
            }

            if (!embeddedManifestBytes.AsSpan().SequenceEqual(adjacentManifestBytes))
            {
                throw new YabtFormatZipException(
                    $"ZIP package '{packageArtifact.RelativePath}' embedded and adjacent manifests differ.");
            }

            foreach (var manifestEntry in manifest.Entries)
            {
                if (!payloadEntries.Remove(manifestEntry.StoredPath, out var entry) ||
                    entry.Length != manifestEntry.Length)
                {
                    throw new YabtFormatZipException(
                        $"ZIP package '{packageArtifact.RelativePath}' does not match manifest " +
                            $"entry '{manifestEntry.StoredPath}'.");
                }
            }

            if (payloadEntries.Count != 0)
            {
                var unexpectedPath = payloadEntries.Keys.Min(StringComparer.Ordinal);
                throw new YabtFormatZipException(
                    $"ZIP package '{packageArtifact.RelativePath}' contains unexpected entry " +
                        $"'{unexpectedPath}'.");
            }

            retainStagedPackage = true;
            return stagedPackage;
        }
        finally
        {
            if (!retainStagedPackage)
            {
                await stagedPackage.DisposeAsync();
            }
        }
    }

    private static void AppendCanonicalProjection
    (
        XxHash128 hash,
        ArchiveProjectionProvenance? projection
    )
    {
        if (projection is null)
        {
            hash.Append([0]);
            return;
        }

        hash.Append([1]);
        AppendCanonicalString(hash, projection.LogicalPath);
        AppendCanonicalString(hash, projection.Format);
        AppendCanonicalInt64(hash, projection.FormatVersion);
        AppendCanonicalString(hash, projection.ProjectionId);
        AppendCanonicalString(hash, projection.ArtifactRole);
    }

    private static void AppendCanonicalJsonValue(XxHash128 hash, JsonElement value)
    {
        hash.Append([(byte)value.ValueKind]);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                AppendCanonicalInt64(hash, properties.LongLength);
                foreach (var property in properties)
                {
                    AppendCanonicalString(hash, property.Name);
                    AppendCanonicalJsonValue(hash, property.Value);
                }

                break;

            case JsonValueKind.Array:
                var items = value.EnumerateArray().ToArray();
                AppendCanonicalInt64(hash, items.LongLength);
                foreach (var item in items)
                {
                    AppendCanonicalJsonValue(hash, item);
                }

                break;

            case JsonValueKind.String:
                AppendCanonicalString(hash, value.GetString() ?? string.Empty);
                break;

            case JsonValueKind.Number:
                AppendCanonicalString(hash, value.GetRawText());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                hash.Append([value.GetBoolean() ? (byte)1 : (byte)0]);
                break;

            case JsonValueKind.Null:
                break;

            default:
                throw new YabtFormatZipException(
                    "ZIP policy options contain an unsupported JSON value.");
        }
    }

    private static DateTimeOffset GetPackageCreationTimeUtc
    (
        IReadOnlyList<ZipSourceObject> sourceObjects
    ) => sourceObjects.Count == 0 ?
        DateTimeOffset.UnixEpoch :
        sourceObjects.Max(sourceObject => sourceObject.LastModifiedUtc);

    private static DateTimeOffset ToZipEntryLastModifiedUtc(DateTimeOffset value)
    {
        var utcValue = value.ToUniversalTime();
        if (utcValue < MinimumZipLastModifiedUtc)
        {
            return MinimumZipLastModifiedUtc;
        }

        return utcValue > MaximumZipLastModifiedUtc ?
            MaximumZipLastModifiedUtc :
            utcValue;
    }

    private static string CreateManifestChangeFingerprint(string packageFingerprint)
    {
        var separator = packageFingerprint.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0 || separator == packageFingerprint.Length - 1)
        {
            throw new ArgumentException(
                "The package fingerprint must be type-qualified.",
                nameof(packageFingerprint));
        }

        return $"zip-manifest-v1:{packageFingerprint[(separator + 1)..]}";
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
        string? SourceKey,
        string RelativePath,
        long? Length,
        DateTimeOffset LastModifiedUtc,
        string? ChangeFingerprint,
        string? ContentHash,
        ArchiveProjectionProvenance? Projection,
        bool IsEmptyFolderMarker
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

    private sealed record ZipPackageMaterializedContent
    (
        byte[] PackageBytes,
        byte[] ManifestBytes,
        ArchiveManifest Manifest
    );

    private sealed class ZipPackageProjectionMaterialization
    {
        private readonly object _gate = new();
        private readonly Func<CancellationToken, Task<ZipPackageMaterializedContent>>? _factory;
        private Task<ZipPackageMaterializedContent>? _materialization;

        public ZipPackageProjectionMaterialization
        (
            Func<CancellationToken, Task<ZipPackageMaterializedContent>> factory
        )
        {
            _factory = factory;
        }

        public ZipPackageProjectionMaterialization(ZipPackageMaterializedContent materialization)
        {
            _materialization = Task.FromResult(materialization);
            PackageLength = materialization.PackageBytes.LongLength;
            ManifestLength = materialization.ManifestBytes.LongLength;
        }

        public long? ManifestLength { get; }

        public long? PackageLength { get; }

        public Task<ZipPackageMaterializedContent> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _materialization ??= (_factory ??
                    throw new InvalidOperationException(
                        "ZIP package projection has no materialization factory."))
                    .Invoke(cancellationToken);
                return _materialization;
            }
        }
    }

    private sealed class ZipRestoreProjectionMaterialization
    (
        Func<CancellationToken, Task<StagedZipRestorePackage>> _factory
    ) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private StagedZipRestorePackage? _stagedPackage;
        private Task<StagedZipRestorePackage>? _materialization;
        private bool _disposed;

        public async Task ValidateAsync(CancellationToken cancellationToken)
        {
            _ = await GetStagedPackageAsync(cancellationToken);
        }

        public async Task<ArchiveObjectContent> OpenEntryAsync
        (
            string entryFullName,
            CancellationToken cancellationToken
        )
        {
            var stagedPackage = await GetStagedPackageAsync(cancellationToken);
            return await stagedPackage.OpenEntryAsync(
                entryFullName,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed) { return; }

                _disposed = true;
                if (_stagedPackage is not null)
                {
                    await _stagedPackage.DisposeAsync();
                    _stagedPackage = null;
                }
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private async Task<StagedZipRestorePackage> GetStagedPackageAsync
        (
            CancellationToken cancellationToken
        )
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _materialization ??= _factory(cancellationToken);
                _stagedPackage ??= await _materialization;
                return _stagedPackage;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private sealed class ManifestSizeLimitedMemoryStream
    (
        string _packageName,
        long _maximumLength
    ) : MemoryStream
    {
        public bool LimitExceeded { get; private set; }

        public override void SetLength(long value)
        {
            EnsureLength(value);
            base.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWriteLength(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWriteLength(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync
        (
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            EnsureWriteLength(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync
        (
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            EnsureWriteLength(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            EnsureWriteLength(1);
            base.WriteByte(value);
        }

        private void EnsureWriteLength(int count)
        {
            var resultingLength = Math.Max(
                Length,
                checked(Position + count));
            EnsureLength(resultingLength);
        }

        private void EnsureLength(long length)
        {
            if (length <= _maximumLength) { return; }

            LimitExceeded = true;
            throw new IOException(
                $"ZIP package manifest for '{_packageName}' exceeded its {_maximumLength} " +
                    "byte output limit.");
        }
    }
}
