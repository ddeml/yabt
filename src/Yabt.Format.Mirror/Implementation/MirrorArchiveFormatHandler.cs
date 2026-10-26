using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Mirror.Implementation;

internal sealed class MirrorArchiveFormatHandler
(
    ILogger<MirrorArchiveFormatHandler> _logger
) : IArchiveFormatHandler, IArchiveBackupFolderProjector, IArchiveRestoreFolderProjector
{
    public string FormatName => MirrorArchiveFormatName.Value;

    public bool ProjectsBesideSourceFolder => false;

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
            "Per-folder mirror options are not supported and will be ignored. " +
                "The mirror representation uses its default behavior.",
            source
        );
    }

    public bool CanRestoreArtifact(ArchiveProjectedObject artifact)
    {
        _logger.LogTrace(nameof(CanRestoreArtifact));

        ArgumentNullException.ThrowIfNull(artifact);
        return true;
    }

    public ArchiveBackupProjection ProjectBackupAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectBackupAsync));
        return new(ProjectBackupObjectsAsync(request, cancellationToken));
    }

    public async Task<ArchiveBackupWorkUnit> ProjectBackupFolderAsync
    (
        ArchiveProjectionRequest request,
        string relativeFolderPath,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectBackupFolderAsync));
        ArgumentNullException.ThrowIfNull(request);

        var relativeFolder = ObjectPath.Parse(relativeFolderPath);
        var sourceRoot = ObjectPath.Parse(request.SourcePrefix);
        var sourceFolder = sourceRoot.Combine(relativeFolder);
        if (relativeFolder.IsRoot)
        {
            ValidateBackupPolicy
            (
                request.Policy ?? FolderPolicy.Default,
                sourceRoot.Combine(ObjectPath.Parse(".yabt-policy.json")).Value
            );
            await request.SourceStore.EnsureReadyAsync(cancellationToken);
        }

        var objects = new List<ArchiveProjectedObject>();
        var children = new List<string>();
        var sourceItems = request.SourceStore.GetFolderItemsAsync
        (
            sourceFolder.Value, recursive: false, cancellationToken
        );
        await foreach (var sourceItem in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ObjectPath.Parse(sourceItem.Key);
            if (sourcePath.IsRoot || sourcePath.Parent != sourceFolder)
            {
                throw new YabtFormatMirrorException
                (
                    $"Folder '{sourceFolder}' returned non-child item '{sourcePath}'."
                );
            }
            var relativePath = ArchiveLayout.RemovePrefix(sourcePath.Value, sourceRoot.Value);
            if (sourceItem.IsFolder)
            {
                children.Add(relativePath);
                continue;
            }
            objects.Add(CreateProjectedObject
            (
                request.SourceStore, sourceItem.Object!, sourcePath.Value, relativePath
            ));
            _logger.LogMirrorProjectedObject(sourcePath.Value, relativePath);
        }

        if (objects.Count == 0 && children.Count == 0)
        {
            objects.Add(CreateEmptyFolderMarker(sourceRoot.Value, sourceFolder.Value));
        }
        return new(relativeFolder.Value, objects, children);
    }

    private async IAsyncEnumerable<ArchiveProjectedObject> ProjectBackupObjectsAsync
    (
        ArchiveProjectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectBackupAsync));

        ArgumentNullException.ThrowIfNull(request);

        ValidateBackupPolicy
        (
            request.Policy ?? FolderPolicy.Default,
            ArchiveLayout.CombinePrefixAndRelativePath(request.SourcePrefix, ".yabt-policy.json")
        );

        await request.SourceStore.EnsureReadyAsync(cancellationToken);

        var sourcePrefix = ArchiveLayout.NormalizeObjectPrefix(request.SourcePrefix);
        var projectedObjectCount = 0;
        var projectedObjects = ProjectFolderAsync(
            request.SourceStore,
            sourcePrefix,
            sourcePrefix,
            cancellationToken);

        await foreach (var projectedObject in projectedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return projectedObject;
            projectedObjectCount++;
        }

        _logger.LogMirrorProjectionCompleted(projectedObjectCount);
    }

    public Task<ArchiveRestoreProjection> ProjectRestoreAsync
    (
        ArchiveRestoreRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectRestoreAsync));

        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var artifact = request.Artifact;
        var relativePath = ArchiveLayout.NormalizeObjectKey(artifact.RelativePath);
        if (string.IsNullOrEmpty(relativePath))
        {
            throw new YabtFormatMirrorException(
                "A mirror restore artifact must have a nonempty relative path.");
        }

        if (IsEmptyFolderMarker(relativePath))
        {
            var parentSeparator = relativePath.LastIndexOf('/');
            var directoryPath = parentSeparator < 0 ?
                string.Empty :
                relativePath[..parentSeparator];
            return Task.FromResult(new ArchiveRestoreProjection(
                directories: string.IsNullOrEmpty(directoryPath) ? [] : [directoryPath]));
        }

        return Task.FromResult(new ArchiveRestoreProjection(
            objects: [artifact with { RelativePath = relativePath }]));
    }

    public Task<ArchiveRestoreProjection> ProjectRestoreFolderAsync
    (
        IReadOnlyList<ArchiveProjectedObject> artifacts,
        string relativeFolderPath,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectRestoreFolderAsync));
        var folder = ObjectPath.Parse(relativeFolderPath);
        var files = new List<ArchiveProjectedObject>();
        var directories = new List<string>();
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ObjectPath.Parse(artifact.RelativePath);
            if (path.IsRoot || path.Parent != folder || artifact.Projection is not null)
            {
                throw new YabtFormatMirrorException
                (
                    $"Artifact '{path}' is not a plain immediate member of folder '{folder}'."
                );
            }
            if (IsEmptyFolderMarker(path.Value))
            {
                if (!folder.IsRoot) { directories.Add(folder.Value); }
            }
            else
            {
                files.Add(artifact);
            }
        }
        return Task.FromResult(new ArchiveRestoreProjection(files, directories));
    }

    private async IAsyncEnumerable<ArchiveProjectedObject> ProjectFolderAsync
    (
        IReadOnlyObjectStore sourceStore,
        string? sourcePrefix,
        string? folderPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken
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
                var childProjectedObjects = ProjectFolderAsync(
                    sourceStore,
                    sourcePrefix,
                    sourceItem.Key,
                    cancellationToken);

                await foreach (var childProjectedObject in childProjectedObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    yield return childProjectedObject;
                }

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

            yield return CreateProjectedObject(
                sourceStore,
                sourceItem.Object,
                sourceKey,
                relativePath);

            _logger.LogMirrorProjectedObject(sourceKey, relativePath);
        }

        if (!hasItems)
        {
            yield return CreateEmptyFolderMarker(sourcePrefix, folderPrefix);
        }
    }

    private static ArchiveProjectedObject CreateProjectedObject
    (
        IReadOnlyObjectStore sourceStore,
        ArchiveObjectInfo sourceObject,
        string sourceKey,
        string relativePath
    )
    {
        var changeFingerprint = GetChangeFingerprint(sourceObject);

        return new
        (
            relativePath,
            cancellationToken => sourceStore.OpenReadAsync(
                sourceKey,
                cancellationToken),
            sourceObject.ContentLength,
            sourceObject.LastModifiedUtc,
            sourceObject.ContentHash,
            changeFingerprint,
            sourceObject.Projection
        );
    }

    private static string? GetChangeFingerprint(ArchiveObjectInfo sourceObject)
    {
        if (!string.IsNullOrWhiteSpace(sourceObject.ChangeFingerprint))
        {
            return sourceObject.ChangeFingerprint;
        }

        ArchiveChangeFingerprint.TryCreate(
            sourceObject.ContentLength,
            sourceObject.LastModifiedUtc,
            out var changeFingerprint);
        return changeFingerprint;
    }

    private static ArchiveProjectedObject CreateEmptyFolderMarker
    (
        string? sourcePrefix,
        string? folderPrefix
    )
    {
        var folderRelativePath = ArchiveLayout.RemovePrefix(
            ArchiveLayout.NormalizeObjectKey(folderPrefix),
            sourcePrefix);
        var markerRelativePath = ArchiveLayout.CombinePrefixAndRelativePath(
            folderRelativePath,
            ArchiveFolderMarkerFileNames.EmptyFolder);

        return new
        (
            markerRelativePath,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(new ArchiveObjectContent(
                    new MemoryStream([], writable: false)));
            },
            0,
            ChangeFingerprint: "yabt-empty-v1:present"
        );
    }

    private static bool IsEmptyFolderMarker(string relativePath) =>
        string.Equals(
            relativePath,
            ArchiveFolderMarkerFileNames.EmptyFolder,
            StringComparison.Ordinal) ||
        relativePath.EndsWith(
            $"/{ArchiveFolderMarkerFileNames.EmptyFolder}",
            StringComparison.Ordinal);
}
