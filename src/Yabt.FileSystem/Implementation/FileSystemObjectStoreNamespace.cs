using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemObjectStoreNamespace : ObjectStoreNamespace
{
    private readonly string _rootPath;
    private readonly FileSystemPathBackend _backend;
    private readonly bool _allowRootTemporaryDirectoryAlias;

    public FileSystemObjectStoreNamespace
    (
        string rootPath,
        FileSystemPathBackend? backend = null,
        bool allowRootTemporaryDirectoryAlias = false
    )
    {
        _rootPath = Path.GetFullPath(rootPath);
        _backend = backend ?? FileSystemPathBackend.Current;
        _allowRootTemporaryDirectoryAlias = allowRootTemporaryDirectoryAlias;
        PathComparer = new FileSystemObjectPathComparer(_rootPath, _backend);
    }

    public override StringComparer PathComparer { get; }

    public override IObjectStoreNamespace AfterRemoving(IEnumerable<ObjectPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var removedPaths = paths.Select
        (
            path =>
            {
                if (path.IsRoot)
                {
                    throw new YabtFileSystemException
                    (
                        "The filesystem namespace root cannot be removed during reconciliation.",
                        path: _rootPath
                    );
                }

                ValidatePath(path);
                return Path.Combine(_rootPath, path.Value.Replace('/', Path.DirectorySeparatorChar));
            }
        ).ToArray();
        return removedPaths.Length == 0 ? this : new FileSystemObjectStoreNamespace
        (
            _rootPath,
            new ProspectiveFileSystemPathBackend(_rootPath, _backend, removedPaths),
            _allowRootTemporaryDirectoryAlias
        );
    }

    public override void ValidatePath(ObjectPath path)
    {
        try
        {
            ValidatePathCore(path);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Object path '{path.Value}' cannot be represented safely in filesystem root '{_rootPath}'.",
                key: path.Value,
                path: _rootPath,
                innerException: ex
            );
        }
    }

    private void ValidatePathCore(ObjectPath path)
    {
        var parent = _rootPath;
        if (path.IsRoot)
        {
            _ = _backend.GetDirectoryNameComparer(parent);
            return;
        }

        foreach (var segment in path.Value.Split('/'))
        {
            _backend.ValidateName(segment);
            _ = _backend.GetDirectoryNameComparer(parent);
            var allowTemporaryAlias = _allowRootTemporaryDirectoryAlias &&
                string.Equals(parent, _rootPath, StringComparison.Ordinal) &&
                string.Equals(segment, ArchiveInternalFolderNames.TemporaryUploads, StringComparison.OrdinalIgnoreCase);
            _backend.ValidateExistingName(parent, segment, allowTemporaryAlias);
            parent = Path.Combine(parent, segment);
        }
    }

    public static bool NativePathsEqual(string first, string second) =>
        IsNativePathUnder(first, second) && IsNativePathUnder(second, first);

    /// <summary>
    /// Tests known containment. With unknown comparison allowed, false can also mean that
    /// an ancestor's name semantics are unsupported; destination validation remains strict.
    /// </summary>
    public static bool IsNativePathUnder
    (
        string candidatePath,
        string ancestorPath,
        bool allowUnknownComparison = false,
        FileSystemPathBackend? backend = null
    )
    {
        var candidate = Path.GetFullPath(candidatePath);
        var ancestor = Path.GetFullPath(ancestorPath);
        var candidateRoot = Path.GetPathRoot(candidate) ?? string.Empty;
        var ancestorRoot = Path.GetPathRoot(ancestor) ?? string.Empty;
        var effectiveBackend = backend ?? FileSystemPathBackend.Current;
        if (!string.Equals(candidateRoot, ancestorRoot, effectiveBackend.NativePathComparison)) { return false; }

        var candidateRelativePath = Path.GetRelativePath(candidateRoot, candidate);
        var ancestorRelativePath = Path.GetRelativePath(ancestorRoot, ancestor);
        var candidateSegments = candidateRelativePath == "." ? [] : candidateRelativePath.Split(Path.DirectorySeparatorChar);
        var ancestorSegments = ancestorRelativePath == "." ? [] : ancestorRelativePath.Split(Path.DirectorySeparatorChar);
        if (candidateSegments.Length < ancestorSegments.Length) { return false; }

        var parentPath = candidateRoot;
        for (var index = 0; index < ancestorSegments.Length; index++)
        {
            if (!string.Equals(candidateSegments[index], ancestorSegments[index], StringComparison.Ordinal))
            {
                StringComparer? comparer;
                if (allowUnknownComparison)
                {
                    if (!effectiveBackend.TryGetDirectoryNameComparer(parentPath, out comparer)) { return false; }
                }
                else
                {
                    comparer = effectiveBackend.GetDirectoryNameComparer(parentPath);
                }

                if (!comparer.Equals(candidateSegments[index], ancestorSegments[index])) { return false; }
            }

            parentPath = Path.Combine(parentPath, candidateSegments[index]);
        }

        return true;
    }
}
