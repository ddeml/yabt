using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipArchiveFormatHandler
(
    ILogger<ZipArchiveFormatHandler> _logger,
    IOptionsMonitor<ZipArchiveFormatOptions> _options,
    IManifestSerializer _manifestSerializer,
    ITemporaryContentFactory _temporaryContentFactory,
    IOptionsMonitor<TemporaryContentOptions> _temporaryOptions
) : IArchiveFormatHandler
{
    private const int DefaultHashBufferSize = 81_920;
    private const string EmptyFolderMarkerFingerprint = "yabt-empty-v1:present";

    private static readonly byte[] PackageFingerprintDomain =
        Encoding.UTF8.GetBytes("yabt-zip-change-v2");

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

    public void ValidateBackupPolicy
    (
        FolderPolicy policy,
        string? configurationSource = null
    )
    {
        _logger.LogTrace(nameof(ValidateBackupPolicy));

        var source = configurationSource ?? ".yabt-policy.json";
        FolderPolicyDiagnostics.ReportIgnoredPatterns(policy, _logger, source);
        FolderPolicyDiagnostics.ReportIgnoredOptions
        (
            policy,
            _logger,
            "Per-folder ZIP options are not supported and will be ignored. " +
                "Application-wide ZIP settings remain effective.",
            source
        );
    }

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

    public ArchiveBackupProjection ProjectBackupAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectBackupAsync));
        var lifetime = new ArchiveProjectionLifetime();
        return new(ProjectBackupObjectsAsync(request, lifetime, cancellationToken), lifetime);
    }

    private async IAsyncEnumerable<ArchiveProjectedObject> ProjectBackupObjectsAsync
    (
        ArchiveProjectionRequest request,
        ArchiveProjectionLifetime lifetime,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectBackupAsync));

        ArgumentNullException.ThrowIfNull(request);

        var policy = request.Policy ?? new FolderPolicy(ZipArchiveFormatName.Value);
        ValidateBackupPolicy
        (
            policy,
            ArchiveLayout.CombinePrefixAndRelativePath(request.SourcePrefix, ".yabt-policy.json")
        );

        await request.SourceStore.EnsureReadyAsync(cancellationToken);

        var index = TemporaryIndex.Create(_temporaryOptions.CurrentValue);
        lifetime.Add(index);
        var sourceEntries = await ListSourceObjectsAsync(request, index, cancellationToken);
        IEnumerable<ZipSourceObject> sourceObjects = sourceEntries.EnumerateOrdered();
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
                index,
                compressionLevel,
                cancellationToken
            );
            lifetime.Add(prebuiltPackage);
            sourceObjects = prebuiltPackage.SourceObjects;
        }

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
                _logger,
                cancellationToken => BuildMaterializedPackageAsync
                (
                    request.SourceStore,
                    sourceObjects,
                    index,
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
                _logger,
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
        lifetime.Add(materialization);
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
        var stagedPackage = await StageRestorePackageAsync(artifact, cancellationToken);
        ArchiveManifest? manifest = null;
        var retained = false;
        try
        {
            if (!stagedPackage.Entries.ContainsKey(ArchivePackageManifestFileNames.EmbeddedEntryName))
            {
                throw new YabtFormatZipException($"ZIP package '{artifact.RelativePath}' has no embedded package manifest.");
            }
            _logger.LogZipEmbeddedManifestRead(artifact.RelativePath);
            await using (var manifestContent = await stagedPackage.OpenEntryAsync(
                ArchivePackageManifestFileNames.EmbeddedEntryName, cancellationToken))
            {
                manifest = await _manifestSerializer.ReadAsync(manifestContent.Content, cancellationToken);
            }
            ValidateRestoreManifest(manifest, artifact);
            ValidatePackageEntries(stagedPackage, manifest, artifact.RelativePath, cancellationToken);
            var result = await CreateRestoreProjectionAsync(
                manifest, packageOutputPrefix, stagedPackage.OpenEntryAsync, stagedPackage,
                adjacentManifest: null, validate: null, cancellationToken);
            retained = true;
            return result;
        }
        catch (Exception ex)
        {
            if (ex is YabtFormatZipException) { throw; }
            throw new YabtFormatZipException(
                $"ZIP package '{artifact.RelativePath}' could not be safely prepared for restore.", ex);
        }
        finally
        {
            if (!retained)
            {
                try { manifest?.Dispose(); }
                finally { await stagedPackage.DisposeAsync(); }
            }
        }
    }

    private async Task<StagedZipRestorePackage> StageRestorePackageAsync
    (
        ArchiveProjectedObject artifact,
        CancellationToken cancellationToken
    )
    {
        var stagedContent = _temporaryContentFactory.Create();
        TemporaryIndex? index = null;
        var retained = false;
        try
        {
            var hash = new XxHash128();
            long contentLength = 0;
            _logger.LogZipRestoreArtifactRead(artifact.RelativePath);
            await using var packageContent = await artifact.OpenContentAsync(cancellationToken);
            var buffer = new byte[DefaultHashBufferSize];
            while (true)
            {
                var bytesRead = await packageContent.Content.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) { break; }
                hash.Append(buffer.AsSpan(0, bytesRead));
                contentLength = checked(contentLength + bytesRead);
                await stagedContent.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
            if (artifact.ContentLength.HasValue && artifact.ContentLength.Value != contentLength)
            {
                throw new YabtFormatZipException(
                    $"ZIP package '{artifact.RelativePath}' has length {contentLength}, but {artifact.ContentLength.Value} bytes were expected.");
            }
            if (!string.Equals(artifact.ContentHash, ArchiveHash.Format(hash.GetHashAndReset()), StringComparison.Ordinal))
            {
                throw new YabtFormatZipException($"ZIP package '{artifact.RelativePath}' failed its content hash check.");
            }
            await stagedContent.SealAsync(cancellationToken);
            _logger.LogZipTemporaryPlumbingOperation(
                stagedContent.IsFileBacked ? "Sealed restore package staging on disk" : "Sealed restore package staging in memory",
                artifact.RelativePath);
            index = TemporaryIndex.Create(_temporaryOptions.CurrentValue);
            var entries = index.CreateDictionary<ZipEntryRecord>();
            await using (var package = stagedContent.OpenRead())
            {
                foreach (var entry in IndexedZipReader.ReadEntries(package, cancellationToken))
                {
                    ValidateZipEntry(entry, artifact.RelativePath);
                    var normalized = NormalizeZipEntryPath(entry.FullName);
                    if (entry.Name.Length == 0 || !string.Equals(normalized, entry.FullName, StringComparison.Ordinal) ||
                        !entries.TryAdd(entry.FullName, entry))
                    {
                        throw new YabtFormatZipException(
                            $"ZIP package '{artifact.RelativePath}' contains unsupported or duplicate entry '{entry.FullName}'.");
                    }
                }
            }
            var result = new StagedZipRestorePackage(stagedContent, index, entries, artifact.RelativePath, _logger);
            retained = true;
            return result;
        }
        catch (Exception ex)
        {
            if (ex is YabtFormatZipException or OperationCanceledException) { throw; }
            throw new YabtFormatZipException($"ZIP package '{artifact.RelativePath}' could not be staged for restore.", ex);
        }
        finally
        {
            if (!retained)
            {
                try { index?.Dispose(); }
                finally { await stagedContent.DisposeAsync(); }
            }
        }
    }

    private static void ValidatePackageEntries
    (
        StagedZipRestorePackage package,
        ArchiveManifest manifest,
        string packagePath,
        CancellationToken cancellationToken
    )
    {
        if (!package.Entries.ContainsKey(ArchivePackageManifestFileNames.EmbeddedEntryName))
        {
            throw new YabtFormatZipException($"ZIP package '{packagePath}' has no embedded package manifest.");
        }
        long count = 0;
        foreach (var manifestEntry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!package.Entries.TryGetValue(manifestEntry.StoredPath, out var entry) || entry.Length != manifestEntry.Length)
            {
                throw new YabtFormatZipException($"ZIP package '{packagePath}' does not match manifest entry '{manifestEntry.StoredPath}'.");
            }
            count++;
        }
        if (count != package.Entries.LongCount - 1)
        {
            throw new YabtFormatZipException($"ZIP package '{packagePath}' contains unexpected entries absent from its manifest.");
        }
    }

    private async Task<ArchiveRestoreProjection> CreateRestoreProjectionAsync
    (
        ArchiveManifest manifest,
        string outputPrefix,
        Func<string, CancellationToken, Task<ArchiveObjectContent>> openEntry,
        IAsyncDisposable materialization,
        TemporaryContent? adjacentManifest,
        Func<CancellationToken, Task>? validate,
        CancellationToken cancellationToken
    )
    {
        var index = TemporaryIndex.Create(_temporaryOptions.CurrentValue);
        var lifetime = new ZipRestoreProjectionLifetime(materialization, manifest, index, adjacentManifest);
        try
        {
            using var filePaths = index.CreateSet();
            var directoryPaths = index.CreateSet();
            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var restorePath = ArchiveLayout.CombinePrefixAndRelativePath(outputPrefix, entry.RelativePath);
                if (string.Equals(entry.Kind, ArchiveManifestEntryKinds.Directory, StringComparison.Ordinal))
                {
                    AddRestoreDirectory(restorePath, filePaths, directoryPaths);
                }
                else
                {
                    AddRestoreFilePath(restorePath, filePaths, directoryPaths);
                }
            }
            if (filePaths.LongCount == 0 && validate is not null) { await validate(cancellationToken); }
            return new ArchiveRestoreProjection(EnumerateFiles(), directoryPaths, lifetime, validate);
        }
        catch (Exception)
        {
            await lifetime.DisposeAsync();
            throw;
        }

        IEnumerable<ArchiveProjectedObject> EnumerateFiles()
        {
            foreach (var entry in manifest.Entries)
            {
                if (string.Equals(entry.Kind, ArchiveManifestEntryKinds.Directory, StringComparison.Ordinal)) { continue; }
                var projection = entry.Projection is null ? null : entry.Projection with
                {
                    LogicalPath = ArchiveLayout.CombinePrefixAndRelativePath(outputPrefix, entry.Projection.LogicalPath)
                };
                yield return new
                (
                    ArchiveLayout.CombinePrefixAndRelativePath(outputPrefix, entry.RelativePath),
                    cancellationToken => openEntry(entry.StoredPath, cancellationToken),
                    entry.Length, entry.LastModifiedUtc, entry.ContentHash,
                    GetRestoreChangeFingerprint(entry), projection
                );
            }
        }
    }

    private static void ValidateZipEntry
    (
        ZipEntryRecord entry,
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

        var packageFileName = ObjectPath.Parse(artifact.RelativePath).Name;
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
        DiskSet filePaths,
        DiskSet directoryPaths
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
        DiskSet filePaths,
        DiskSet directoryPaths
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
        var fileName = ObjectPath.Parse(relativePath).Name;
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

    private async Task<DiskEntryCollection<ZipSourceObject>> ListSourceObjectsAsync
    (
        ArchiveProjectionRequest request,
        TemporaryIndex index,
        CancellationToken cancellationToken
    )
    {
        var sourcePrefix = ArchiveLayout.NormalizeObjectPrefix(request.SourcePrefix);
        var sourceObjects = index.CreateCollection<ZipSourceObject>();
        await AddFolderSourceObjectsAsync
        (
            request.SourceStore,
            sourcePrefix,
            sourcePrefix,
            sourceObjects,
            cancellationToken
        );

        return sourceObjects;
    }

    private async Task AddFolderSourceObjectsAsync
    (
        IReadOnlyObjectStore sourceStore,
        string? sourcePrefix,
        string? folderPrefix,
        DiskEntryCollection<ZipSourceObject> sourceObjects,
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
            ), relativePath);
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
        ), markerRelativePath);
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
        IEnumerable<ZipSourceObject> sourceObjects,
        TemporaryIndex index,
        CompressionLevel compressionLevel,
        CancellationToken cancellationToken
    )
    {
        var package = _temporaryContentFactory.Create();
        var directory = _temporaryContentFactory.Create();
        var archive = new StreamingZipWriter(package, directory);
        var completedSourceObjects = index.CreateCollection<ZipSourceObject>();
        var sourceReadFailures = new List<Exception>();
        byte[]? hashBuffer = null;
        try
        {
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
                            compressionLevel,
                            ToZipEntryLastModifiedUtc(sourceObject.LastModifiedUtc)
                        );
                        await using var markerContent = markerEntry;
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
                        if (ObjectStoreOverlapException.IsContainedIn(ex)) { throw; }
                        sourceReadFailures.Add(ex);
                        continue;
                    }

                    await using (sourceContent)
                    {
                        var entry = archive.CreateEntry
                        (
                            sourceObject.RelativePath,
                            compressionLevel,
                            ToZipEntryLastModifiedUtc(sourceObject.LastModifiedUtc)
                        );
                        await using var entryContent = entry;
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
                                if (ObjectStoreOverlapException.IsContainedIn(ex)) { throw; }
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
                            ContentHash = ArchiveHash.Format(hash.GetCurrentHash()),
                            ChangeFingerprint = sourceObject.ChangeFingerprint ?? ArchiveHash.Format(hash.GetCurrentHash()),
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

            return new
            (
                package,
                directory,
                archive,
                completedSourceObjects
            );
        }
        catch (Exception)
        {
            try { archive.Dispose(); }
            finally
            {
                try { await directory.DisposeAsync(); }
                finally { await package.DisposeAsync(); }
                completedSourceObjects.Dispose();
            }
            throw;
        }
    }

    private async Task<ZipPackageMaterializedContent> BuildMaterializedPackageAsync
    (
        IReadOnlyObjectStore sourceStore,
        IEnumerable<ZipSourceObject> sourceObjects,
        TemporaryIndex index,
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
            index,
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
                    sourcePath));
            using var manifest = _manifestSerializer.Create
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
            var serializedManifest = _temporaryContentFactory.Create();
            try
            {
                await _manifestSerializer.WriteAsync(manifest, serializedManifest, cancellationToken);
                await serializedManifest.SealAsync(cancellationToken);
                var manifestEntry = package.Archive.CreateEntry
                (
                    ArchivePackageManifestFileNames.EmbeddedEntryName,
                    compressionLevel,
                    ToZipEntryLastModifiedUtc(createdAtUtc)
                );
                await using (var manifestEntryContent = manifestEntry)
                await using (var manifestContent = serializedManifest.OpenRead())
                {
                    await manifestContent.CopyToAsync(manifestEntryContent, cancellationToken);
                }
                await package.Archive.CompleteAsync(cancellationToken);
                package.Archive.Dispose();
                await package.Directory.DisposeAsync();
                package.SourceObjects.Dispose();
                await package.Content.SealAsync(cancellationToken);
                return new(package.Content, serializedManifest);
            }
            catch (Exception)
            {
                await serializedManifest.DisposeAsync();
                throw;
            }
        }
        catch (Exception)
        {
            await package.DisposeAsync();
            throw;
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
                package.PackageContent.OpenRead(),
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
                package.ManifestContent.OpenRead(),
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
        IEnumerable<ZipSourceObject> sourceObjects,
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
        }
        else
        {
            hash.Append([1]);
            var options = JsonSerializer.SerializeToElement(policy.Options);
            AppendCanonicalJsonValue(hash, options);
        }

        if (policy.DirectoryLinks is not { Count: > 0 } directoryLinks) { return; }

        AppendCanonicalString(hash, "directory-links-v1");
        AppendCanonicalInt64(hash, directoryLinks.Count);
        var orderedLinks = directoryLinks.OrderBy(pair => pair.Key, StringComparer.Ordinal);
        foreach (var pair in orderedLinks)
        {
            AppendCanonicalString(hash, pair.Key);
            AppendCanonicalString(hash, pair.Value.Behavior);
            AppendCanonicalString(hash, pair.Value.ExpectedTarget);
        }
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
        var manifestContent = await ReadAndValidateManifestArtifactAsync(manifestArtifact, cancellationToken);
        ArchiveManifest? manifest = null;
        ZipRestoreProjectionMaterialization? materialization = null;
        var retained = false;
        try
        {
            await using (var content = manifestContent.OpenRead())
            {
                manifest = await _manifestSerializer.ReadAsync(content, cancellationToken);
            }
            ValidateRestoreManifest(manifest, packageArtifact);
            var packageProjection = packageArtifact.Projection ??
                throw new YabtFormatZipException($"ZIP package '{packageArtifact.RelativePath}' has no projection provenance.");
            if (!string.Equals(manifest.SourcePath, packageProjection.LogicalPath, StringComparison.Ordinal))
            {
                throw new YabtFormatZipException(
                    $"ZIP package '{packageArtifact.RelativePath}' has a logical path that differs from its adjacent manifest.");
            }
            materialization = new ZipRestoreProjectionMaterialization(
                cancellationToken => StageAndValidateRestorePackageAsync(
                    packageArtifact, manifest, manifestContent, cancellationToken));
            var result = await CreateRestoreProjectionAsync(
                manifest, manifest.SourcePath, materialization.OpenEntryAsync,
                materialization, manifestContent, materialization.ValidateAsync, cancellationToken);
            retained = true;
            return result;
        }
        finally
        {
            if (!retained)
            {
                try
                {
                    if (materialization is not null) { await materialization.DisposeAsync(); }
                }
                finally
                {
                    try { manifest?.Dispose(); }
                    finally { await manifestContent.DisposeAsync(); }
                }
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

    private async Task<TemporaryContent> ReadAndValidateManifestArtifactAsync
    (
        ArchiveProjectedObject manifestArtifact,
        CancellationToken cancellationToken
    )
    {
        if (!ArchiveHash.IsValid(manifestArtifact.ContentHash) || manifestArtifact.ContentLength is < 0)
        {
            throw new YabtFormatZipException(
                $"Adjacent package manifest '{manifestArtifact.RelativePath}' does not have valid content evidence.");
        }
        var content = _temporaryContentFactory.Create();
        try
        {
            await using var artifact = await manifestArtifact.OpenContentAsync(cancellationToken);
            var hash = new XxHash128();
            var buffer = new byte[DefaultHashBufferSize];
            long length = 0;
            while (true)
            {
                var count = await artifact.Content.ReadAsync(buffer, cancellationToken);
                if (count == 0) { break; }
                length = checked(length + count);
                hash.Append(buffer.AsSpan(0, count));
                await content.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (manifestArtifact.ContentLength.HasValue && manifestArtifact.ContentLength.Value != length ||
                !string.Equals(manifestArtifact.ContentHash, ArchiveHash.Format(hash.GetHashAndReset()), StringComparison.Ordinal))
            {
                throw new YabtFormatZipException(
                    $"Adjacent package manifest '{manifestArtifact.RelativePath}' failed its content evidence check.");
            }
            await content.SealAsync(cancellationToken);
            return content;
        }
        catch (Exception)
        {
            await content.DisposeAsync();
            throw;
        }
    }

    private async Task<StagedZipRestorePackage> StageAndValidateRestorePackageAsync
    (
        ArchiveProjectedObject packageArtifact,
        ArchiveManifest manifest,
        TemporaryContent adjacentManifest,
        CancellationToken cancellationToken
    )
    {
        var staged = await StageRestorePackageAsync(packageArtifact, cancellationToken);
        var retained = false;
        try
        {
            ValidatePackageEntries(staged, manifest, packageArtifact.RelativePath, cancellationToken);
            _logger.LogZipEmbeddedManifestRead(packageArtifact.RelativePath);
            await using var embedded = await staged.OpenEntryAsync(
                ArchivePackageManifestFileNames.EmbeddedEntryName, cancellationToken);
            await using var adjacent = adjacentManifest.OpenRead();
            var embeddedBuffer = new byte[DefaultHashBufferSize];
            var adjacentBuffer = new byte[DefaultHashBufferSize];
            while (true)
            {
                var count = await embedded.Content.ReadAsync(embeddedBuffer, cancellationToken);
                var adjacentCount = await adjacent.ReadAtLeastAsync(
                    adjacentBuffer.AsMemory(0, count == 0 ? 1 : count), count == 0 ? 1 : count,
                    throwOnEndOfStream: false, cancellationToken);
                if (count != adjacentCount || !embeddedBuffer.AsSpan(0, count).SequenceEqual(adjacentBuffer.AsSpan(0, adjacentCount)))
                {
                    throw new YabtFormatZipException(
                        $"ZIP package '{packageArtifact.RelativePath}' embedded and adjacent manifests differ.");
                }
                if (count == 0) { break; }
            }
            retained = true;
            return staged;
        }
        finally
        {
            if (!retained) { await staged.DisposeAsync(); }
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
        IEnumerable<ZipSourceObject> sourceObjects
    ) => sourceObjects.Select(sourceObject => sourceObject.LastModifiedUtc)
        .DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max();

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
        var sourceName = ObjectPath.Parse(sourceDisplayName ?? sourcePrefix).Name;
        var packageSourceName = string.IsNullOrEmpty(sourceName) ? "root" : sourceName;
        return $"{packageSourceName}.{fileNameHash}.zip";
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
        TemporaryContent Content,
        TemporaryContent Directory,
        StreamingZipWriter Archive,
        DiskEntryCollection<ZipSourceObject> SourceObjects
    ) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { Archive.Dispose(); }
            finally
            {
                try { await Directory.DisposeAsync(); }
                finally { await Content.DisposeAsync(); }
                SourceObjects.Dispose();
            }
        }
    }

    private sealed record ZipPackageMaterializedContent
    (
        TemporaryContent PackageContent,
        TemporaryContent ManifestContent
    ) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await PackageContent.DisposeAsync(); }
            finally { await ManifestContent.DisposeAsync(); }
        }
    }

    private sealed class ZipPackageProjectionMaterialization : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly ILogger _logger;
        private Func<CancellationToken, Task<ZipPackageMaterializedContent>>? _factory;
        private bool _disposed;
        private Task<ZipPackageMaterializedContent>? _materialization;

        public ZipPackageProjectionMaterialization
        (
            ILogger logger,
            Func<CancellationToken, Task<ZipPackageMaterializedContent>> factory
        )
        {
            _logger = logger;
            _factory = factory;
        }

        public ZipPackageProjectionMaterialization(ILogger logger, ZipPackageMaterializedContent materialization)
        {
            _logger = logger;
            _materialization = Task.FromResult(materialization);
            PackageLength = materialization.PackageContent.Length;
            ManifestLength = materialization.ManifestContent.Length;
        }

        public long? ManifestLength { get; }

        public long? PackageLength { get; }

        public Task<ZipPackageMaterializedContent> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _materialization ??= (_factory ??
                    throw new InvalidOperationException(
                        "ZIP package projection has no materialization factory."))
                    .Invoke(cancellationToken);
                return _materialization;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task<ZipPackageMaterializedContent>? materialization;
            lock (_gate)
            {
                if (_disposed) { return; }
                _disposed = true;
                materialization = _materialization;
                _materialization = null;
                _factory = null;
            }
            // Never release backing storage underneath an in-flight materialization.
            if (materialization is not null)
            {
                ZipPackageMaterializedContent content;
                try { content = await materialization; }
                catch (Exception ex)
                {
                    // The materialization owns partial-write cleanup; its caller observes failure.
                    _logger.LogDebug(ex, "Ignoring an already observed ZIP materialization failure during disposal.");
                    return;
                }
                await content.DisposeAsync();
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

}
