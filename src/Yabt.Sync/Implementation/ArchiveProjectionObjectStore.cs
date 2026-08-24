using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveProjectionObjectStore
(
    IReadOnlyObjectStore _inner,
    string _sourceRoot,
    string? sourceRootPrefix,
    IFolderPolicyReader _folderPolicyReader,
    IReadOnlyDictionary<string, IArchiveFormatHandler> _formatHandlers,
    string? logicalRootPath = default
) : IReadOnlyObjectStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ArchiveProjectedObject> _projectedObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectedScopeDefinition> _projectedScopeDefinitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectedScope> _projectedScopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openingProjectedObjectKeys = new(StringComparer.Ordinal);
    private readonly List<string> _projectedSourcePrefixes = [];
    private readonly string? _sourceRootPrefix = ArchiveLayout.NormalizeObjectPrefix(sourceRootPrefix);
    private readonly string _logicalRootPath = ArchiveLayout.NormalizeObjectKey(logicalRootPath);

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

        if (projectedObject is null)
        {
            return await _inner.OpenReadAsync(
                normalizedKey,
                cancellationToken);
        }

        try
        {
            return await projectedObject.OpenContentAsync(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _openingProjectedObjectKeys.Remove(normalizedKey);
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
        var projectedScope = await GetProjectedScopeAsync(
            projectedScopePrefix,
            cancellationToken);
        return CreateProjectedFolderItems(
            projectedScope,
            folderPrefix,
            cancellationToken);
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
            projectedScopeDefinition.SourceDisplayName,
            projectedScopePrefix,
            _folderPolicyReader,
            _formatHandlers,
            projectedScopeDefinition.LogicalPath
        );
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

        await foreach (var projectedObject in streamedProjectedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await RegisterProjectedObjectAsync(
                projectedScopePrefix,
                outputPrefix,
                formatHandler.ProjectsBesideSourceFolder,
                projectedObject,
                cancellationToken);
            projectedObjects.Add(projectedObject);
        }

        if (formatHandler.ProjectsBesideSourceFolder)
        {
            ValidatePackagedProjection(
                projectedScopePrefix,
                projectedScopeDefinition,
                projectedObjects);
        }

        var projectedScope = new ProjectedScope
        (
            outputPrefix,
            projectedObjects
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
        foreach (var projectedObject in projectedObjects)
        {
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

        var sourceDisplayName = CreateSourceDisplayName(projectedScopePrefix);
        var logicalPath = ArchiveLayout.CombinePrefixAndRelativePath(
            _logicalRootPath,
            ArchiveLayout.RemovePrefix(
                projectedScopePrefix,
                _sourceRootPrefix));
        var policy = await _folderPolicyReader.ReadPolicyAsync(
            sourceDisplayName,
            cancellationToken);
        var formatHandler = ResolveFormatHandler(policy);
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
            if (_projectedSourcePrefixes.Contains(
                    normalizedSourcePrefix,
                    StringComparer.Ordinal))
            {
                return false;
            }

            _projectedSourcePrefixes.Add(normalizedSourcePrefix);
            return true;
        }
    }

    private string? FindProjectedScopePrefix(string? folderPrefix)
    {
        var normalizedFolderPrefix = ArchiveLayout.NormalizeObjectKey(folderPrefix);
        lock (_gate)
        {
            foreach (var projectedSourcePrefix in _projectedSourcePrefixes.OrderByDescending(
                         prefix => prefix.Length))
            {
                if (ArchiveLayout.IsUnderPrefix(
                        normalizedFolderPrefix,
                        projectedSourcePrefix))
                {
                    return projectedSourcePrefix;
                }
            }
        }

        return null;
    }

    private bool IsUnderProjectedSourcePrefix(string objectKey)
    {
        lock (_gate)
        {
            foreach (var projectedSourcePrefix in _projectedSourcePrefixes)
            {
                if (ArchiveLayout.IsUnderPrefix(
                        objectKey,
                        projectedSourcePrefix))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetParentPrefix(string objectKey)
    {
        var normalizedObjectKey = ArchiveLayout.NormalizeObjectKey(objectKey);
        var separator = normalizedObjectKey.LastIndexOf('/');

        return separator < 0 ? string.Empty : normalizedObjectKey[..separator];
    }

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
        if (string.IsNullOrEmpty(sourceRelativePath))
        {
            return _sourceRoot;
        }

        var nativeRelativePath = sourceRelativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        return Path.Combine(
            _sourceRoot,
            nativeRelativePath);
    }

    private sealed record ProjectedScope
    (
        string OutputPrefix,
        IReadOnlyList<ArchiveProjectedObject> Objects
    );

    private sealed record ProjectedScopeDefinition
    (
        string SourceDisplayName,
        string LogicalPath,
        FolderPolicy Policy,
        IArchiveFormatHandler FormatHandler
    );
}
