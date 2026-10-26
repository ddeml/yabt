using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveProjectionObjectStore
(
    IReadOnlyObjectStore _inner,
    string _sourceDisplayName,
    string? sourceRootPrefix,
    Func<string, CancellationToken, Task<FolderPolicy>> _readPolicyAsync,
    IReadOnlyDictionary<string, IArchiveFormatHandler> _formatHandlers,
    string? logicalRootPath = default
) : IReadOnlyObjectStore, IAsyncDisposable
{
    private readonly ArchiveProjectionLifetime _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, ArchiveProjectedObject> _projectedObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectedScopeDefinition> _projectedScopeDefinitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectedScope> _projectedScopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FolderProjectedScope> _folderProjectedScopes = new(StringComparer.Ordinal);
    private readonly Dictionary<RestoreProjectionKey, ProjectionArtifactGroup> _projectionArtifactGroups = [];
    private readonly HashSet<string> _openingProjectedObjectKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _projectedSourcePrefixes = new(StringComparer.Ordinal);
    private readonly string? _sourceRootPrefix = ArchiveLayout.NormalizeObjectPrefix(sourceRootPrefix);
    private readonly string _logicalRootPath = ArchiveLayout.NormalizeObjectKey(logicalRootPath);

    public IObjectStoreNamespace Namespace => _inner.Namespace;

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        return _inner.EnsureReadyAsync(cancellationToken);
    }

    public async Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
        ArchiveProjectedObject? projectedObject;
        lock (_gate)
        {
            if (!_projectedObjects.TryGetValue(normalizedKey, out projectedObject))
            {
                projectedObject = null;
            }
            else if (!_openingProjectedObjectKeys.Add(normalizedKey))
            {
                // A projected object may read a source object with the same key while building its own content.
                // In that reentrant case, use the inner source object to avoid recursively opening itself.
                projectedObject = null;
            }
        }

        try
        {
            var openedContent = projectedObject is null ?
                await _inner.OpenReadAsync(
                    normalizedKey,
                    cancellationToken) :
                await projectedObject.OpenContentAsync(cancellationToken);
            return openedContent with
            {
                Content = new SourceObjectReadTrackingStream
                (
                    openedContent.Content,
                    CreateSourceDisplayName(normalizedKey),
                    normalizedKey
                ),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (SourceObjectReadException.FindAll(exception).Count > 0)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SourceObjectReadException
            (
                CreateSourceDisplayName(normalizedKey),
                normalizedKey,
                exception
            );
        }
        finally
        {
            if (projectedObject is not null)
            {
                lock (_gate)
                {
                    _openingProjectedObjectKeys.Remove(normalizedKey);
                }
            }
        }
    }

    public Task<bool> ExistsAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
        lock (_gate)
        {
            if (_projectedObjects.ContainsKey(normalizedKey))
            {
                return Task.FromResult(true);
            }
        }

        return _inner.ExistsAsync(
            normalizedKey,
            cancellationToken);
    }

    public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
    (
        string? folderPrefix,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var normalizedPrefix = ArchiveLayout.NormalizeObjectPrefix(folderPrefix);
        if (recursive)
        {
            var recursiveItems = GetRecursiveFolderItemsAsync(
                normalizedPrefix,
                cancellationToken);

            await foreach (var recursiveItem in recursiveItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return recursiveItem;
            }

            yield break;
        }

        var projectedScopePrefix = FindProjectedScopePrefix(normalizedPrefix);
        if (projectedScopePrefix is not null)
        {
            var projectedItems = await GetProjectedFolderItemsAsync(
                projectedScopePrefix,
                normalizedPrefix,
                cancellationToken);
            foreach (var projectedItem in projectedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return projectedItem;
            }

            yield break;
        }

        var sourceItems = _inner.GetFolderItemsAsync(
            normalizedPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var sourceItem in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceItem.IsFolder &&
                !IsSourceRootPrefix(sourceItem.Key) &&
                await HasPolicyAsync(sourceItem.Key, cancellationToken))
            {
                var projectedSourcePrefix = ArchiveLayout.NormalizeObjectKey(sourceItem.Key);
                TryAddProjectedSourcePrefix(projectedSourcePrefix);
                var projectedScopeDefinition = await GetProjectedScopeDefinitionAsync(
                    projectedSourcePrefix,
                    cancellationToken);

                if (projectedScopeDefinition.FormatHandler.ProjectsBesideSourceFolder)
                {
                    var projectedScope = await GetProjectedScopeAsync(
                        projectedSourcePrefix,
                        projectedScopeDefinition,
                        cancellationToken);
                    var projectedItems = CreateProjectedFolderItems(
                        projectedScope,
                        normalizedPrefix,
                        cancellationToken);
                    foreach (var projectedItem in projectedItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        yield return projectedItem;
                    }

                    continue;
                }

                yield return sourceItem with
                {
                    Key = projectedSourcePrefix,
                };
                continue;
            }

            if (sourceItem.IsObject &&
                string.Equals(
                    sourceItem.Name,
                    FolderPolicyFileNames.Primary,
                    StringComparison.Ordinal) &&
                IsUnderProjectedSourcePrefix(sourceItem.Key))
            {
                continue;
            }

            yield return sourceItem with
            {
                Key = ArchiveLayout.NormalizeObjectKey(sourceItem.Key),
                Object = sourceItem.Object is null ? null : sourceItem.Object with
                {
                    Key = ArchiveLayout.NormalizeObjectKey(sourceItem.Object.Key),
                },
            };
        }
    }

    private async IAsyncEnumerable<ArchiveFolderItem> GetRecursiveFolderItemsAsync
    (
        string? folderPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var folderItems = GetFolderItemsAsync(
            folderPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var folderItem in folderItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (folderItem.IsFolder)
            {
                var childItems = GetRecursiveFolderItemsAsync(
                    folderItem.Key,
                    cancellationToken);

                await foreach (var childItem in childItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    yield return childItem;
                }

                continue;
            }

            yield return folderItem;
        }
    }

    private async Task<IReadOnlyList<ArchiveFolderItem>> GetProjectedFolderItemsAsync
    (
        string projectedScopePrefix,
        string? folderPrefix,
        CancellationToken cancellationToken
    )
    {
        var definition = await GetProjectedScopeDefinitionAsync(projectedScopePrefix, cancellationToken);
        if (!definition.FormatHandler.ProjectsBesideSourceFolder &&
            definition.FormatHandler is IArchiveBackupFolderProjector folderProjector)
        {
            return await GetLocalProjectedFolderItemsAsync
            (
                projectedScopePrefix, folderPrefix, definition, folderProjector, cancellationToken
            );
        }
        var projectedScope = await GetProjectedScopeAsync(
            projectedScopePrefix,
            cancellationToken);
        return CreateProjectedFolderItems(
            projectedScope,
            folderPrefix,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ArchiveFolderItem>> GetLocalProjectedFolderItemsAsync
    (
        string scopePrefix,
        string? folderPrefix,
        ProjectedScopeDefinition definition,
        IArchiveBackupFolderProjector folderProjector,
        CancellationToken cancellationToken
    )
    {
        FolderProjectedScope scope;
        lock (_gate)
        {
            if (!_folderProjectedScopes.TryGetValue(scopePrefix, out scope!))
            {
                var source = new ArchiveProjectionObjectStore
                (
                    _inner, CreateSourceDisplayName(scopePrefix), scopePrefix,
                    _readPolicyAsync, _formatHandlers, definition.LogicalPath
                );
                var scopeLifetime = new ArchiveProjectionLifetime();
                scopeLifetime.Add(source);
                _lifetime.Add(scopeLifetime);
                scope = new
                (
                    source,
                    new(source, scopePrefix, definition.Policy, definition.SourceDisplayName, definition.LogicalPath),
                    scopeLifetime,
                    new(StringComparer.Ordinal)
                );
                _folderProjectedScopes.Add(scopePrefix, scope);
            }
        }

        var relativeFolder = ArchiveLayout.RemovePrefix
        (
            ArchiveLayout.NormalizeObjectKey(folderPrefix), scopePrefix
        );
        if (scope.Folders.TryGetValue(relativeFolder, out var cachedListing))
        {
            return CreateLocalProjectedFolderItems(cachedListing);
        }

        await using var unit = await folderProjector.ProjectBackupFolderAsync
        (
            scope.Request, relativeFolder, cancellationToken
        );
        if (!string.Equals(unit.RelativePath, relativeFolder, StringComparison.Ordinal))
        {
            throw new YabtSyncException($"Folder projection returned the wrong scope '{unit.RelativePath}'.");
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var objectKeys = new List<string>();
        var childKeys = new List<string>();
        foreach (var projectedObject in unit.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateLocalProjectedPath(projectedObject.RelativePath, relativeFolder, paths);
            await RegisterProjectedObjectAsync
            (
                scopePrefix, scopePrefix, projectsBesideSourceFolder: false, projectedObject, cancellationToken
            );
            CopyProjectionArtifactCount(scope.SourceStore, projectedObject.Projection);
            var key = ArchiveLayout.CombinePrefixAndRelativePath(scopePrefix, projectedObject.RelativePath);
            objectKeys.Add(key);
        }
        foreach (var child in unit.ChildFolderPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateLocalProjectedPath(child, relativeFolder, paths);
            var key = ArchiveLayout.CombinePrefixAndRelativePath(scopePrefix, child);
            childKeys.Add(key);
        }
        var lifetime = unit.DetachLifetime();
        if (lifetime is not null) { scope.Lifetime.Add(lifetime); }
        var listing = new FolderProjectionListing(objectKeys, childKeys);
        scope.Folders.Add(relativeFolder, listing);
        return CreateLocalProjectedFolderItems(listing);
    }

    private List<ArchiveFolderItem> CreateLocalProjectedFolderItems(FolderProjectionListing listing)
    {
        var items = new List<ArchiveFolderItem>();
        foreach (var key in listing.ObjectKeys)
        {
            var projectedObject = _projectedObjects[key];
            items.Add(ArchiveFolderItem.CreateObject
            (
                ObjectPath.Parse(key).Name,
                new
                (
                    key, projectedObject.ContentLength, projectedObject.LastModifiedUtc,
                    projectedObject.ContentHash, projectedObject.ChangeFingerprint, projectedObject.Projection
                )
            ));
        }
        foreach (var key in listing.ChildKeys)
        {
            items.Add(ArchiveFolderItem.CreateFolder(ObjectPath.Parse(key).Name, key));
        }
        return items;
    }

    private static void ValidateLocalProjectedPath
    (
        string path,
        string folder,
        HashSet<string> paths
    )
    {
        var objectPath = ObjectPath.Parse(path);
        if (objectPath.IsRoot || !string.Equals(objectPath.Parent.Value, folder, StringComparison.Ordinal) ||
            !paths.Add(path))
        {
            throw new YabtSyncException
            (
                $"Folder projection '{folder}' produced a duplicate or non-child path '{path}'."
            );
        }
    }

    private static ArchiveFolderItem[] CreateProjectedFolderItems
    (
        ProjectedScope projectedScope,
        string? folderPrefix,
        CancellationToken cancellationToken
    )
    {
        var folderItems = new Dictionary<string, ArchiveFolderItem>(StringComparer.Ordinal);

        foreach (var projectedObject in projectedScope.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectedKey = ArchiveLayout.CombinePrefixAndRelativePath(
                projectedScope.OutputPrefix,
                projectedObject.RelativePath);
            if (!ArchiveLayout.IsUnderPrefix(projectedKey, folderPrefix))
            {
                continue;
            }

            var relativePath = ArchiveLayout.RemovePrefix(
                projectedKey,
                folderPrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            var separator = relativePath.IndexOf('/');
            if (separator >= 0)
            {
                var folderName = relativePath[..separator];
                folderItems.TryAdd(
                    folderName,
                    ArchiveFolderItem.CreateFolder(
                        folderName,
                        ArchiveLayout.CombinePrefixAndRelativePath(
                            folderPrefix,
                            folderName)));
                continue;
            }

            var archiveObject = new ArchiveObjectInfo
            (
                projectedKey,
                projectedObject.ContentLength,
                projectedObject.LastModifiedUtc,
                projectedObject.ContentHash,
                projectedObject.ChangeFingerprint,
                projectedObject.Projection
            );
            folderItems.TryAdd(
                relativePath,
                ArchiveFolderItem.CreateObject(
                    relativePath,
                    archiveObject));
        }

        return folderItems.Values.ToArray();
    }

    private async Task<ProjectedScope> GetProjectedScopeAsync
    (
        string projectedScopePrefix,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            if (_projectedScopes.TryGetValue(projectedScopePrefix, out var cachedProjectedScope))
            {
                return cachedProjectedScope;
            }
        }

        var projectedScopeDefinition = await GetProjectedScopeDefinitionAsync(
            projectedScopePrefix,
            cancellationToken);
        return await GetProjectedScopeAsync(
            projectedScopePrefix,
            projectedScopeDefinition,
            cancellationToken);
    }

    private async Task<ProjectedScope> GetProjectedScopeAsync
    (
        string projectedScopePrefix,
        ProjectedScopeDefinition projectedScopeDefinition,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            if (_projectedScopes.TryGetValue(projectedScopePrefix, out var cachedProjectedScope))
            {
                return cachedProjectedScope;
            }
        }

        var formatHandler = projectedScopeDefinition.FormatHandler;
        var outputPrefix = formatHandler.ProjectsBesideSourceFolder ?
            GetParentPrefix(projectedScopePrefix) :
            ArchiveLayout.NormalizeObjectKey(projectedScopePrefix);
        var projectedSourceStore = new ArchiveProjectionObjectStore
        (
            _inner,
            CreateSourceDisplayName(projectedScopePrefix),
            projectedScopePrefix,
            _readPolicyAsync,
            _formatHandlers,
            projectedScopeDefinition.LogicalPath
        );
        var scopeLifetime = new ArchiveProjectionLifetime();
        _lifetime.Add(scopeLifetime);
        scopeLifetime.Add(projectedSourceStore);
        var request = new ArchiveProjectionRequest
        (
            projectedSourceStore,
            projectedScopePrefix,
            projectedScopeDefinition.Policy,
            projectedScopeDefinition.SourceDisplayName,
            projectedScopeDefinition.LogicalPath
        );
        var projectedObjects = new List<ArchiveProjectedObject>();
        var streamedProjectedObjects = formatHandler.ProjectBackupAsync(
            request,
            cancellationToken);
        scopeLifetime.Add(streamedProjectedObjects);

        await foreach (var projectedObject in streamedProjectedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await RegisterProjectedObjectAsync(
                projectedScopePrefix,
                outputPrefix,
                formatHandler.ProjectsBesideSourceFolder,
                projectedObject,
                cancellationToken);
            CopyProjectionArtifactCount(projectedSourceStore, projectedObject.Projection);
            projectedObjects.Add(projectedObject);
        }

        // Reject file/prefix collisions before the flat projection is converted
        // to immediate folder entries, where a duplicate name would hide one side.
        ObjectStoreNamespace.Ordinal.ValidateTree
        (
            projectedObjects.Select(projectedObject => ObjectPath.Parse(projectedObject.RelativePath)),
            []
        );

        if (formatHandler.ProjectsBesideSourceFolder)
        {
            ValidatePackagedProjection(
                projectedScopePrefix,
                projectedScopeDefinition,
                projectedObjects);
            var projection = projectedObjects[0].Projection!;
            lock (_gate)
            {
                _projectionArtifactGroups[GetProjectionKey(projection)] = new
                (
                    projectedObjects.Count, Lifetime: scopeLifetime
                );
            }
        }

        var projectedScope = new ProjectedScope
        (
            outputPrefix,
            projectedObjects,
            scopeLifetime,
            projectedSourceStore,
            formatHandler.ProjectsBesideSourceFolder
        );
        lock (_gate)
        {
            _projectedScopes[projectedScopePrefix] = projectedScope;
        }

        return projectedScope;
    }

    private static void ValidatePackagedProjection
    (
        string projectedScopePrefix,
        ProjectedScopeDefinition definition,
        List<ArchiveProjectedObject> projectedObjects
    )
    {
        if (projectedObjects.Count == 0)
        {
            throw new YabtSyncException(
                $"Packaged source folder '{projectedScopePrefix}' did not produce any artifacts.");
        }

        string? projectionId = null;
        int? formatVersion = null;
        var artifactRoles = new HashSet<string>(StringComparer.Ordinal);
        var sourceScope = ObjectPath.Parse(projectedScopePrefix);
        var outputParent = sourceScope.Parent;
        foreach (var projectedObject in projectedObjects)
        {
            var outputPath = outputParent.Combine(ObjectPath.Parse(projectedObject.RelativePath));
            if (outputPath.Parent != outputParent && !outputPath.IsUnder(sourceScope))
            {
                throw new YabtSyncException
                (
                    $"Packaged source folder '{projectedScopePrefix}' produced nested artifact " +
                        $"'{projectedObject.RelativePath}' outside its registered folder scope. " +
                        "Package artifacts must be immediate siblings of the source folder " +
                        "or descendants of that folder."
                );
            }
            var projection = projectedObject.Projection ??
                throw new YabtSyncException(
                    $"Packaged source folder '{projectedScopePrefix}' produced artifact " +
                        $"'{projectedObject.RelativePath}' without projection provenance.");
            if (!string.Equals(
                    projection.LogicalPath,
                    definition.LogicalPath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    projection.Format,
                    definition.FormatHandler.FormatName,
                    StringComparison.Ordinal))
            {
                throw new YabtSyncException(
                    $"Packaged source folder '{projectedScopePrefix}' produced inconsistent " +
                        "projection provenance.");
            }

            projectionId ??= projection.ProjectionId;
            formatVersion ??= projection.FormatVersion;
            if (!string.Equals(
                    projectionId,
                    projection.ProjectionId,
                    StringComparison.Ordinal) ||
                formatVersion != projection.FormatVersion ||
                !artifactRoles.Add(projection.ArtifactRole))
            {
                throw new YabtSyncException(
                    $"Packaged source folder '{projectedScopePrefix}' produced artifacts from " +
                        "multiple projections or with duplicate roles.");
            }
        }
    }

    private async Task<ProjectedScopeDefinition> GetProjectedScopeDefinitionAsync
    (
        string projectedScopePrefix,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            if (_projectedScopeDefinitions.TryGetValue(
                    projectedScopePrefix,
                    out var cachedProjectedScopeDefinition))
            {
                return cachedProjectedScopeDefinition;
            }
        }

        var sourceDisplayName = ObjectPath.Parse(projectedScopePrefix).Name;
        var logicalPath = ArchiveLayout.CombinePrefixAndRelativePath(
            _logicalRootPath,
            ArchiveLayout.RemovePrefix(
                projectedScopePrefix,
                _sourceRootPrefix));
        var policy = await _readPolicyAsync(projectedScopePrefix, cancellationToken);
        var formatHandler = ResolveFormatHandler(policy);
        formatHandler.ValidateBackupPolicy
        (
            policy,
            ArchiveLayout.CombinePrefixAndRelativePath(projectedScopePrefix, FolderPolicyFileNames.Primary)
        );
        var projectedScopeDefinition = new ProjectedScopeDefinition
        (
            sourceDisplayName,
            logicalPath,
            policy,
            formatHandler
        );
        lock (_gate)
        {
            if (_projectedScopeDefinitions.TryGetValue(
                    projectedScopePrefix,
                    out var cachedProjectedScopeDefinition))
            {
                return cachedProjectedScopeDefinition;
            }

            _projectedScopeDefinitions.Add(
                projectedScopePrefix,
                projectedScopeDefinition);
        }

        return projectedScopeDefinition;
    }

    private IArchiveFormatHandler ResolveFormatHandler(FolderPolicy policy)
    {
        if (_formatHandlers.TryGetValue(policy.Format, out var formatHandler))
        {
            return formatHandler;
        }

        throw new YabtSyncException($"No archive format handler is registered for format '{policy.Format}'.");
    }

    private async Task RegisterProjectedObjectAsync
    (
        string projectedSourcePrefix,
        string projectedOutputPrefix,
        bool projectsBesideSourceFolder,
        ArchiveProjectedObject projectedObject,
        CancellationToken cancellationToken
    )
    {
        if (projectsBesideSourceFolder && projectedObject.Projection is null)
        {
            throw new YabtSyncException(
                $"Packaged source folder '{projectedSourcePrefix}' produced artifact " +
                    $"'{projectedObject.RelativePath}' without projection provenance.");
        }

        var projectedKey = ArchiveLayout.CombinePrefixAndRelativePath(
            projectedOutputPrefix,
            projectedObject.RelativePath);
        var normalizedProjectedKey = ArchiveLayout.NormalizeObjectKey(projectedKey);
        if (projectsBesideSourceFolder &&
            await HasConflictingSourceItemAsync(
                normalizedProjectedKey,
                cancellationToken))
        {
            throw new YabtSyncException(
                $"Projected object '{normalizedProjectedKey}' for source folder " +
                $"'{projectedSourcePrefix}' conflicts with a source item in the parent folder.");
        }

        lock (_gate)
        {
            if (!_projectedObjects.TryAdd(
                    normalizedProjectedKey,
                    projectedObject))
            {
                throw new YabtSyncException(
                    $"Source folder '{projectedSourcePrefix}' projects to duplicate object " +
                    $"key '{normalizedProjectedKey}'.");
            }
        }
    }

    private async Task<bool> HasConflictingSourceItemAsync
    (
        string projectedKey,
        CancellationToken cancellationToken
    )
    {
        if (await _inner.ExistsAsync(projectedKey, cancellationToken))
        {
            return true;
        }

        var parentPrefix = GetParentPrefix(projectedKey);
        var sourceItems = _inner.GetFolderItemsAsync(
            parentPrefix,
            recursive: false,
            cancellationToken);
        await foreach (var sourceItem in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(
                    ArchiveLayout.NormalizeObjectKey(sourceItem.Key),
                    projectedKey,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAddProjectedSourcePrefix(string sourcePrefix)
    {
        var normalizedSourcePrefix = ArchiveLayout.NormalizeObjectKey(sourcePrefix);
        if (string.IsNullOrEmpty(normalizedSourcePrefix))
        {
            return false;
        }

        lock (_gate)
        {
            return _projectedSourcePrefixes.Add(normalizedSourcePrefix);
        }
    }

    private string? FindProjectedScopePrefix(string? folderPrefix)
    {
        var path = ObjectPath.Parse(folderPrefix);
        lock (_gate)
        {
            while (!path.IsRoot)
            {
                if (_projectedSourcePrefixes.Contains(path.Value)) { return path.Value; }
                path = path.Parent;
            }
        }

        return null;
    }

    private bool IsUnderProjectedSourcePrefix(string objectKey) =>
        FindProjectedScopePrefix(objectKey) is not null;

    private static string GetParentPrefix(string objectKey) => ObjectPath.Parse(objectKey).Parent.Value;

    private bool IsSourceRootPrefix(string objectKey)
    {
        var normalizedKey = ArchiveLayout.NormalizeObjectKey(objectKey);
        var normalizedRootPrefix = ArchiveLayout.NormalizeObjectKey(_sourceRootPrefix);

        return string.Equals(
            normalizedKey,
            normalizedRootPrefix,
            StringComparison.Ordinal);
    }

    private async Task<bool> HasPolicyAsync
    (
        string sourcePrefix,
        CancellationToken cancellationToken
    )
    {
        return await _inner.ExistsAsync(
            ArchiveLayout.CombinePrefixAndRelativePath(
                sourcePrefix,
                FolderPolicyFileNames.Primary),
            cancellationToken);
    }

    private string CreateSourceDisplayName(string sourcePrefix)
    {
        var sourceRelativePath = ArchiveLayout.RemovePrefix(
            sourcePrefix,
            _sourceRootPrefix);
        return ObjectPath.Parse(_sourceDisplayName).Combine(ObjectPath.Parse(sourceRelativePath)).Value;
    }

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();

    internal bool TryGetProjectionArtifactCount(ArchiveProjectionProvenance projection, out int count)
    {
        lock (_gate)
        {
            if (_projectionArtifactGroups.TryGetValue(GetProjectionKey(projection), out var group))
            {
                count = group.ArtifactCount;
                return true;
            }
        }
        count = 0;
        return false;
    }

    private void CopyProjectionArtifactCount
    (
        ArchiveProjectionObjectStore source,
        ArchiveProjectionProvenance? projection
    )
    {
        if (projection is null || !source.TryGetProjectionArtifactCount(projection, out var count)) { return; }
        lock (_gate)
        {
            _projectionArtifactGroups.TryAdd(GetProjectionKey(projection), new(count, SourceStore: source));
        }
    }

    private static RestoreProjectionKey GetProjectionKey(ArchiveProjectionProvenance projection) => new
    (
        projection.LogicalPath, projection.Format, projection.FormatVersion, projection.ProjectionId
    );

    public async ValueTask ReleaseProjectionAsync(ArchiveProjectionProvenance projection)
    {
        ProjectionArtifactGroup? group;
        lock (_gate) { _projectionArtifactGroups.TryGetValue(GetProjectionKey(projection), out group); }
        if (group?.Lifetime is not null)
        {
            await group.Lifetime.DisposeAsync();
        }
        else if (group?.SourceStore is not null)
        {
            await group.SourceStore.ReleaseProjectionAsync(projection);
        }
    }

    private sealed record ProjectionArtifactGroup
    (
        int ArtifactCount,
        ArchiveProjectionLifetime? Lifetime = null,
        ArchiveProjectionObjectStore? SourceStore = null
    );

    private sealed record FolderProjectedScope
    (
        ArchiveProjectionObjectStore SourceStore,
        ArchiveProjectionRequest Request,
        ArchiveProjectionLifetime Lifetime,
        Dictionary<string, FolderProjectionListing> Folders
    );

    private sealed record FolderProjectionListing
    (
        IReadOnlyList<string> ObjectKeys,
        IReadOnlyList<string> ChildKeys
    );

    private sealed record ProjectedScope
    (
        string OutputPrefix,
        IReadOnlyList<ArchiveProjectedObject> Objects,
        ArchiveProjectionLifetime Lifetime,
        ArchiveProjectionObjectStore SourceStore,
        bool ProjectsBesideSourceFolder
    );

    private sealed record ProjectedScopeDefinition
    (
        string SourceDisplayName,
        string LogicalPath,
        FolderPolicy Policy,
        IArchiveFormatHandler FormatHandler
    );
}
