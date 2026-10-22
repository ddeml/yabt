using System.Buffers;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class RestorePlanner
(
    ILogger<RestorePlanner> _logger,
    IEnumerable<IArchiveFormatHandler> formatHandlers
)
{
    private readonly FrozenDictionary<string, IArchiveFormatHandler> _formatHandlers =
        formatHandlers.ToFrozenDictionary
    (
        formatHandler => formatHandler.FormatName,
        StringComparer.Ordinal
    );

    internal IArchiveFormatHandler ResolveRestoreRootFormatHandler
    (
        ArchiveChangeManifest changeManifest,
        RestoreArchiveObjects restoreObjects
    )
    {
        _logger.LogTrace(nameof(ResolveRestoreRootFormatHandler));

        if (changeManifest.RootFormat is not null)
        {
            if (!_formatHandlers.TryGetValue(changeManifest.RootFormat, out var rootFormatHandler))
            {
                throw new YabtSyncException(
                    "No archive format handler is registered for durable root format " +
                        $"'{changeManifest.RootFormat}'.");
            }

            return rootFormatHandler;
        }

        var packageCount = 0;
        foreach (var restoreObject in restoreObjects)
        {
            if (_formatHandlers.Values.Any(formatHandler =>
                    formatHandler.ProjectsBesideSourceFolder &&
                    formatHandler.CanRestoreArtifact(restoreObject)))
            {
                packageCount++;
            }
        }

        if (packageCount == 0 || restoreObjects.Count > 1)
        {
            var fallbackHandlers = _formatHandlers.Values
                .Where(formatHandler =>
                    !formatHandler.ProjectsBesideSourceFolder &&
                    restoreObjects.All(formatHandler.CanRestoreArtifact))
                .ToArray();
            if (fallbackHandlers.Length == 1)
            {
                return fallbackHandlers[0];
            }

            throw new YabtSyncException(
                "The legacy change manifest does not identify one unambiguous root format handler.");
        }

        throw new YabtSyncException(
            "This archive change manifest predates durable root-format evidence, so YABT " +
                "cannot safely distinguish a packaged root from a sole packaged child folder. " +
                "Run backup once with the current YABT version before restoring it.");
    }

    internal async Task<RestoreArchiveObjects> LoadRestoreObjectsAsync
    (
        RestoreContext context,
        ArchiveChangeManifest changeManifest,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(LoadRestoreObjectsAsync));

        using var restoreIndex = TemporaryIndex.Create();
        using var listedObjects = restoreIndex.CreateDictionary<ArchiveObjectInfo>();
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(context.Descriptor.Layout.LivePrefix);
        var internalKeys = ArchivePathRules.CreateInternalObjectKeys(context.Descriptor.Layout);
        var internalPrefixes = ArchivePathRules.CreateInternalObjectPrefixes(context.Descriptor.Layout);
        var pendingFolders = new Stack<string?>();
        pendingFolders.Push(livePrefix);
        while (pendingFolders.TryPop(out var folderPrefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogObjectRead(
                folderPrefix ?? ".",
                "restore archive live-branch enumeration");
            var liveItems = context.ArchiveStore.GetFolderItemsAsync(
                folderPrefix,
                recursive: false,
                cancellationToken);
            await foreach (var item in liveItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archiveKey = ArchiveLayout.NormalizeObjectKey(item.Key);
                if (ArchivePathRules.IsInternalObjectPrefix(archiveKey, internalPrefixes)) { continue; }

                if (item.Object is null)
                {
                    pendingFolders.Push(archiveKey);
                    continue;
                }

                if (ArchivePathRules.IsInternalObjectKey(archiveKey, internalKeys)) { continue; }

                var relativePath = ArchiveLayout.RemovePrefix(archiveKey, livePrefix);
                if (!listedObjects.TryAdd(relativePath, item.Object))
                {
                    throw new YabtSyncException(
                        $"Archive live object '{relativePath}' was listed more than once.");
                }
            }
        }

        using var manifestEntries = restoreIndex.CreateDictionary<ArchiveChangeManifestEntry>();
        foreach (var entry in changeManifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ArchiveLayout.NormalizeObjectKey(entry.RelativePath);
            if (string.IsNullOrEmpty(relativePath) ||
                !ArchiveHash.IsValid(entry.ContentHash))
            {
                throw new YabtSyncException(
                    $"Change manifest entry '{entry.RelativePath}' does not contain safe restore evidence.");
            }

            if (!manifestEntries.TryAdd(relativePath, entry))
            {
                throw new YabtSyncException(
                    $"Change manifest contains duplicate restore path '{relativePath}'.");
            }
        }

        var unexpectedObject = listedObjects.Enumerate(cancellationToken)
            .Select(pair => pair.Key)
            .FirstOrDefault(relativePath => !manifestEntries.ContainsKey(relativePath));
        if (unexpectedObject is not null)
        {
            throw new YabtSyncException(
                $"Archive contains live object '{unexpectedObject}' that is not covered by the change manifest.");
        }

        var restoreObjects = new RestoreArchiveObjects(context.ArchiveStore, context.Descriptor.Layout);
        try
        {
            foreach (var pair in manifestEntries.Enumerate(cancellationToken))
            {
                var archiveKey = context.Descriptor.Layout.ToLiveObjectKey(pair.Key);
                listedObjects.TryGetValue(pair.Key, out var archiveObjectInfo);
                if (archiveObjectInfo is null &&
                    !await context.ArchiveStore.ExistsAsync(archiveKey, cancellationToken))
                {
                    throw new YabtSyncException(
                        $"Change manifest restore object '{pair.Key}' is missing from the archive.");
                }

                var lastModifiedUtc = archiveObjectInfo?.LastModifiedUtc;
                if (ArchiveChangeFingerprint.TryParse(
                        pair.Value.ChangeFingerprint,
                        out _,
                        out var sourceLastModifiedUtc))
                {
                    lastModifiedUtc = sourceLastModifiedUtc;
                }

                var contentHash = pair.Value.ContentHash ??
                    throw new YabtSyncException(
                        $"Restore object '{pair.Key}' has no content hash.");
                restoreObjects.Add(new
                (
                    pair.Key,
                    archiveObjectInfo?.ContentLength ?? pair.Value.ArtifactLength,
                    lastModifiedUtc,
                    contentHash,
                    pair.Value.ChangeFingerprint,
                    pair.Value.Projection
                ));
            }

            return restoreObjects;
        }
        catch (Exception)
        {
            restoreObjects.Dispose();
            throw;
        }
    }

    internal async Task<RestorePlan> CreateRestorePlanAsync
    (
        RestoreContext context,
        RestoreArchiveObjects restoreObjects,
        IObjectStoreNamespace destinationNamespace,
        bool useProjectionProvenance,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateRestorePlanAsync));

        var pathComparer = StringComparer.Ordinal;
        var filePaths = new HashSet<string>(pathComparer);
        var directoryPaths = new Dictionary<string, string>(pathComparer);
        var files = new List<ArchiveProjectedObject>();
        var fileProjectionDependencies =
            new Dictionary<string, IReadOnlyList<ArchiveRestoreProjection>>(pathComparer);
        var projections = new List<ArchiveRestoreProjection>();
        var rootFormatHandler = context.RootFormatHandler ??
            throw new YabtSyncException("Restore root format handler was not resolved.");

        try
        {
            void AddProjectionDirectories(ArchiveRestoreProjection projection)
            {
                foreach (var directoryPath in projection.Directories)
                {
                    AddRestoreDirectory(
                        directoryPath,
                        destinationNamespace,
                        filePaths,
                        directoryPaths);
                }
            }

            void AddFinalRestoreObject
            (
                string formatName,
                ArchiveProjectedObject projectedObject,
                IReadOnlyList<ArchiveRestoreProjection> projectionDependencies
            )
            {
                if (!ArchiveHash.IsValid(projectedObject.ContentHash))
                {
                    throw new YabtSyncException(
                        $"Archive format handler '{formatName}' produced restore object " +
                            $"'{projectedObject.RelativePath}' without a valid content hash.");
                }

                var relativePath = ArchiveLayout.NormalizeObjectKey(
                    projectedObject.RelativePath);
                AddRestoreFilePath(
                    relativePath,
                    destinationNamespace,
                    filePaths,
                    directoryPaths);
                files.Add(projectedObject with
                {
                    RelativePath = relativePath,
                    Projection = null,
                });
                if (!fileProjectionDependencies.TryAdd(
                        relativePath,
                        projectionDependencies.ToArray()))
                {
                    throw new YabtSyncException(
                        $"Restore path '{relativePath}' has duplicate projection lifetime evidence.");
                }
            }

            async Task<ArchiveRestoreProjection> ProjectArtifactsAsync
            (
                IArchiveFormatHandler formatHandler,
                IEnumerable<ArchiveProjectedObject> artifacts,
                bool restoreAsRoot,
                bool requireCompleteProjection = false
            )
            {
                var artifactSnapshot = artifacts.ToArray();
                try
                {
                    var projection = await formatHandler.ProjectRestoreAsync
                    (
                        new ArchiveRestoreRequest(
                            artifactSnapshot,
                            restoreAsRoot,
                            requireCompleteProjection),
                        cancellationToken
                    );
                    projections.Add(projection);
                    AddProjectionDirectories(projection);
                    return projection;
                }
                catch (Exception ex)
                {
                    if (ex is YabtSyncException) { throw; }

                    var artifactDescription = string.Join(
                        ", ",
                        artifactSnapshot.Select(artifact => $"'{artifact.RelativePath}'"));
                    throw new YabtSyncException(
                        $"Archive format handler '{formatHandler.FormatName}' could not restore " +
                            $"artifact(s) {artifactDescription}: {ex.Message}",
                        ex);
                }
            }

            if (useProjectionProvenance)
            {
                var processedProjectionKeys = new HashSet<RestoreProjectionKey>();

                async Task ProcessObjectsAsync
                (
                    IEnumerable<ArchiveProjectedObject> projectedObjects,
                    bool initialLevel,
                    IReadOnlyList<ArchiveRestoreProjection> projectionDependencies
                )
                {
                    var objectSnapshot = projectedObjects.ToArray();
                    var plainObjects = objectSnapshot
                        .Where(projectedObject => projectedObject.Projection is null)
                        .ToArray();
                    var projectionGroups = objectSnapshot
                        .Where(projectedObject => projectedObject.Projection is not null)
                        .GroupBy(projectedObject =>
                        {
                            var provenance = projectedObject.Projection ??
                                throw new YabtSyncException(
                                    "A grouped restore artifact lost its projection provenance.");
                            return new RestoreProjectionKey(
                                provenance.LogicalPath,
                                provenance.Format,
                                provenance.FormatVersion,
                                provenance.ProjectionId);
                        })
                        .OrderBy(group => group.Key.LogicalPath, StringComparer.Ordinal)
                        .ThenBy(group => group.Key.Format, StringComparer.Ordinal)
                        .ToArray();

                    if (initialLevel && context.RootIsPackaged)
                    {
                        if (plainObjects.Length != 0 ||
                            projectionGroups.Length != 1 ||
                            !string.IsNullOrEmpty(projectionGroups[0].Key.LogicalPath) ||
                            !string.Equals(
                                projectionGroups[0].Key.Format,
                                rootFormatHandler.FormatName,
                                StringComparison.Ordinal))
                        {
                            throw new YabtSyncException(
                                "The live archive does not contain one unambiguous packaged-root " +
                                    "projection. Run backup with the current YABT version before restoring.");
                        }
                    }
                    else if (initialLevel)
                    {
                        foreach (var projectionGroup in projectionGroups)
                        {
                            if (string.IsNullOrEmpty(projectionGroup.Key.LogicalPath))
                            {
                                throw new YabtSyncException(
                                    "A child archive projection must have a nonempty logical path.");
                            }
                        }
                    }

                    foreach (var plainObject in plainObjects)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!initialLevel)
                        {
                            AddFinalRestoreObject(
                                rootFormatHandler.FormatName,
                                plainObject,
                                projectionDependencies);
                            continue;
                        }

                        var projection = await ProjectArtifactsAsync(
                            rootFormatHandler,
                            [plainObject],
                            restoreAsRoot: context.RootIsPackaged);
                        await ProcessObjectsAsync(
                            projection.Objects,
                            initialLevel: false,
                            [.. projectionDependencies, projection]);
                    }

                    foreach (var projectionGroup in projectionGroups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var projectionKey = projectionGroup.Key;
                        if (!processedProjectionKeys.Add(projectionKey))
                        {
                            throw new YabtSyncException(
                                $"Archive projection '{projectionKey.LogicalPath}' with id " +
                                    $"'{projectionKey.ProjectionId}' was encountered more than once.");
                        }

                        if (!_formatHandlers.TryGetValue(
                                projectionKey.Format,
                                out var formatHandler))
                        {
                            throw new YabtSyncException(
                                $"No archive format handler is registered for durable format " +
                                    $"'{projectionKey.Format}'.");
                        }

                        var artifacts = projectionGroup
                            .OrderBy(artifact => artifact.Projection?.ArtifactRole, StringComparer.Ordinal)
                            .ToArray();
                        var duplicateRole = artifacts
                            .GroupBy(
                                artifact => artifact.Projection?.ArtifactRole,
                                StringComparer.Ordinal)
                            .FirstOrDefault(roleGroup => roleGroup.Count() > 1);
                        if (duplicateRole is not null)
                        {
                            throw new YabtSyncException(
                                $"Archive projection '{projectionKey.LogicalPath}' contains duplicate " +
                                    $"artifact role '{duplicateRole.Key}'.");
                        }

                        var restoreAsRoot = initialLevel && context.RootIsPackaged;
                        var projection = await ProjectArtifactsAsync(
                            formatHandler,
                            artifacts,
                            restoreAsRoot,
                            requireCompleteProjection: true);
                        await ProcessObjectsAsync(
                            projection.Objects,
                            initialLevel: false,
                            [.. projectionDependencies, projection]);
                    }
                }

                await ProcessObjectsAsync(
                    restoreObjects,
                    initialLevel: true,
                    projectionDependencies: []);
            }
            else
            {
                if (context.RootIsPackaged &&
                    (restoreObjects.Count != 1 ||
                        !rootFormatHandler.CanRestoreArtifact(restoreObjects[0]) ||
                        !string.IsNullOrEmpty(ArchivePathRules.GetParentPrefix(restoreObjects[0].RelativePath))))
                {
                    throw new YabtSyncException(
                        "The current root policy describes a packaged root, but the live archive " +
                            "does not contain exactly one root package. Back up the source before restoring it.");
                }

                foreach (var restoreObject in restoreObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var formatHandler = ResolveRestoreArtifactFormatHandler(
                        rootFormatHandler,
                        restoreObject,
                        context.RootIsPackaged);
                    var projection = await ProjectArtifactsAsync(
                        formatHandler,
                        [restoreObject],
                        context.RootIsPackaged);
                    foreach (var projectedObject in projection.Objects)
                    {
                        AddFinalRestoreObject(
                            formatHandler.FormatName,
                            projectedObject,
                            [projection]);
                    }
                }
            }

            var directories = directoryPaths.Values
                .OrderBy(ArchivePathRules.GetPathDepth)
                .ThenBy(directoryPath => directoryPath, StringComparer.Ordinal)
                .ToArray();
            var nonemptyDirectories = new HashSet<string>(pathComparer);
            foreach (var file in files)
            {
                var parentPath = ArchivePathRules.GetParentPrefix(file.RelativePath);
                while (!string.IsNullOrEmpty(parentPath))
                {
                    nonemptyDirectories.Add(parentPath);
                    parentPath = ArchivePathRules.GetParentPrefix(parentPath);
                }
            }

            var emptyDirectories = directoryPaths.Values
                .Where(directoryPath => !nonemptyDirectories.Contains(directoryPath))
                .OrderBy(ArchivePathRules.GetPathDepth)
                .ThenBy(directoryPath => directoryPath, StringComparer.Ordinal)
                .ToArray();
            destinationNamespace.ValidateTree
            (
                files.Select(file => ObjectPath.Parse(file.RelativePath)),
                directories.Select(ObjectPath.Parse)
            );
            return new(
                files,
                directories,
                emptyDirectories,
                projections,
                fileProjectionDependencies,
                destinationNamespace);
        }
        catch (Exception)
        {
            foreach (var projection in projections)
            {
                await projection.DisposeAsync();
            }

            throw;
        }
    }

    private IArchiveFormatHandler ResolveRestoreArtifactFormatHandler
    (
        IArchiveFormatHandler rootFormatHandler,
        ArchiveProjectedObject restoreObject,
        bool restoreAsRoot
    )
    {
        _logger.LogTrace(nameof(ResolveRestoreArtifactFormatHandler));

        if (restoreAsRoot)
        {
            if (!rootFormatHandler.CanRestoreArtifact(restoreObject))
            {
                throw new YabtSyncException(
                    $"Root format handler '{rootFormatHandler.FormatName}' does not recognize " +
                        $"archive artifact '{restoreObject.RelativePath}'.");
            }

            return rootFormatHandler;
        }

        var matchingHandlers = _formatHandlers.Values
            .Where(formatHandler =>
                formatHandler.ProjectsBesideSourceFolder &&
                formatHandler.CanRestoreArtifact(restoreObject))
            .ToArray();
        if (matchingHandlers.Length > 1)
        {
            throw new YabtSyncException(
                $"Archive artifact '{restoreObject.RelativePath}' is claimed by multiple " +
                    "format handlers.");
        }

        if (matchingHandlers.Length == 1)
        {
            return matchingHandlers[0];
        }

        if (!rootFormatHandler.CanRestoreArtifact(restoreObject))
        {
            throw new YabtSyncException(
                $"No archive format handler recognizes artifact '{restoreObject.RelativePath}'.");
        }

        return rootFormatHandler;
    }

    private static void AddRestoreFilePath
    (
        string relativePath,
        IObjectStoreNamespace destinationNamespace,
        HashSet<string> filePaths,
        Dictionary<string, string> directoryPaths
    )
    {
        destinationNamespace.ValidatePath(ObjectPath.Parse(relativePath));
        if (!filePaths.Add(relativePath) || directoryPaths.ContainsKey(relativePath))
        {
            throw new YabtSyncException(
                $"Multiple archive items restore to the same path '{relativePath}'.");
        }

        var parentPath = ArchivePathRules.GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(parentPath))
        {
            if (filePaths.Contains(parentPath))
            {
                throw new YabtSyncException(
                    $"Restore file '{relativePath}' conflicts with file '{parentPath}'.");
            }

            AddRestoreDirectoryPath(parentPath, directoryPaths);
            parentPath = ArchivePathRules.GetParentPrefix(parentPath);
        }
    }

    private static void AddRestoreDirectory
    (
        string relativePath,
        IObjectStoreNamespace destinationNamespace,
        HashSet<string> filePaths,
        Dictionary<string, string> directoryPaths
    )
    {
        if (string.IsNullOrEmpty(relativePath)) { return; }

        destinationNamespace.ValidatePath(ObjectPath.Parse(relativePath));
        if (filePaths.Contains(relativePath))
        {
            throw new YabtSyncException(
                $"Restore directory '{relativePath}' conflicts with a file at the same path.");
        }

        AddRestoreDirectoryPath(relativePath, directoryPaths);
        var parentPath = ArchivePathRules.GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(parentPath))
        {
            if (filePaths.Contains(parentPath))
            {
                throw new YabtSyncException(
                    $"Restore directory '{relativePath}' conflicts with file '{parentPath}'.");
            }

            AddRestoreDirectoryPath(parentPath, directoryPaths);
            parentPath = ArchivePathRules.GetParentPrefix(parentPath);
        }
    }

    private static void AddRestoreDirectoryPath
    (
        string directoryPath,
        Dictionary<string, string> directoryPaths
    ) => directoryPaths.TryAdd(directoryPath, directoryPath);

    internal static void ValidateRestorePlanInternalPaths
    (
        RestorePlan restorePlan,
        ArchiveLayout destinationLayout,
        IRestoreDestination restoreTarget
    )
    {
        foreach (var file in restorePlan.Files)
        {
            var destinationPath = destinationLayout.ToLiveObjectKey(file.RelativePath);
            restoreTarget.ValidateRelativePath(destinationPath);
            if (ArchivePathRules.IsInternalObject(destinationPath, destinationLayout) ||
                ArchivePathRules.IsStrictPrefixAncestor(destinationPath, destinationLayout.HistPrefix))
            {
                throw new YabtSyncException(
                    $"Restore path '{file.RelativePath}' conflicts with destination history, " +
                        "metadata, or provider plumbing.");
            }
        }

        foreach (var directoryPath in restorePlan.Directories)
        {
            var destinationPath = destinationLayout.ToLiveObjectKey(directoryPath);
            restoreTarget.ValidateRelativePath(destinationPath);
            if (ArchivePathRules.IsInternalObject(destinationPath, destinationLayout))
            {
                throw new YabtSyncException(
                    $"Restore folder '{directoryPath}' conflicts with destination history or " +
                        "provider plumbing.");
            }
        }
    }

    internal static void ValidateRestoreLayoutPaths
    (
        ArchiveLayout destinationLayout,
        IRestoreDestination restoreTarget
    )
    {
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(destinationLayout.LivePrefix);
        if (livePrefix is not null)
        {
            restoreTarget.ValidateRelativePath(livePrefix);
        }

        restoreTarget.ValidateRelativePath(destinationLayout.HistPrefix);
    }
}
