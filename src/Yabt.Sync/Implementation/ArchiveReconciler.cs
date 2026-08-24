using System.Runtime.CompilerServices;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal static class ArchiveReconciler
{
    public static Task<ArchiveReconciliationTraversal> ReconcileAsync<TExisting>
    (
        IEnumerable<ArchiveProjectedObject> desiredObjects,
        IEnumerable<string>? explicitDirectories,
        StringComparer pathComparer,
        Action<string> validatePath,
        Func<ArchiveProjectedObject, string, CancellationToken, Task<TExisting?>> takeExistingAsync,
        Func<ArchiveProjectedObject, string, TExisting?, CancellationToken, Task> consumeAsync,
        CancellationToken cancellationToken
    )
        where TExisting : class
    {
        var streamedDesiredObjects = StreamDesiredObjectsAsync(
            desiredObjects,
            cancellationToken);
        return ReconcileAsync(
            streamedDesiredObjects,
            explicitDirectories,
            pathComparer,
            validatePath,
            takeExistingAsync,
            consumeAsync,
            cancellationToken);
    }

    public static async Task<ArchiveReconciliationTraversal> ReconcileAsync<TExisting>
    (
        IAsyncEnumerable<ArchiveProjectedObject> desiredObjects,
        IEnumerable<string>? explicitDirectories,
        StringComparer pathComparer,
        Action<string> validatePath,
        Func<ArchiveProjectedObject, string, CancellationToken, Task<TExisting?>> takeExistingAsync,
        Func<ArchiveProjectedObject, string, TExisting?, CancellationToken, Task> consumeAsync,
        CancellationToken cancellationToken
    )
        where TExisting : class
    {
        ArgumentNullException.ThrowIfNull(desiredObjects);
        ArgumentNullException.ThrowIfNull(pathComparer);
        ArgumentNullException.ThrowIfNull(validatePath);
        ArgumentNullException.ThrowIfNull(takeExistingAsync);
        ArgumentNullException.ThrowIfNull(consumeAsync);

        var desiredObjectPaths = new HashSet<string>(pathComparer);
        var desiredFolderPaths = new HashSet<string>(pathComparer);
        if (explicitDirectories is not null)
        {
            foreach (var explicitDirectory in explicitDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = ArchiveLayout.NormalizeObjectKey(explicitDirectory);
                if (string.IsNullOrEmpty(relativePath)) { continue; }

                validatePath(relativePath);
                AddFolderAndAncestors(relativePath, desiredFolderPaths);
            }
        }

        await foreach (var sourceObject in desiredObjects.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ArchiveLayout.NormalizeObjectKey(sourceObject.RelativePath);
            if (string.IsNullOrEmpty(relativePath))
            {
                throw new YabtSyncException("A projected object has an empty relative path.");
            }

            validatePath(relativePath);
            if (!desiredObjectPaths.Add(relativePath))
            {
                throw new YabtSyncException(
                    $"Projected object path '{relativePath}' was produced more than once.");
            }

            AddParentFolders(relativePath, desiredFolderPaths);
            var desiredObject = sourceObject with { RelativePath = relativePath };
            var existingObject = await takeExistingAsync(
                desiredObject,
                relativePath,
                cancellationToken);
            await consumeAsync(
                desiredObject,
                relativePath,
                existingObject,
                cancellationToken);
        }

        return new(desiredObjectPaths, desiredFolderPaths);
    }

    private static async IAsyncEnumerable<ArchiveProjectedObject> StreamDesiredObjectsAsync
    (
        IEnumerable<ArchiveProjectedObject> desiredObjects,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var desiredObject in desiredObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return desiredObject;
        }

        await Task.CompletedTask;
    }

    private static void AddFolderAndAncestors
    (
        string relativePath,
        HashSet<string> desiredFolderPaths
    )
    {
        var folderPath = relativePath;
        while (!string.IsNullOrEmpty(folderPath))
        {
            desiredFolderPaths.Add(folderPath);
            folderPath = GetParentPrefix(folderPath);
        }
    }

    private static void AddParentFolders
    (
        string relativePath,
        HashSet<string> desiredFolderPaths
    )
    {
        var parentPath = GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(parentPath))
        {
            desiredFolderPaths.Add(parentPath);
            parentPath = GetParentPrefix(parentPath);
        }
    }

    private static string GetParentPrefix(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }
}
