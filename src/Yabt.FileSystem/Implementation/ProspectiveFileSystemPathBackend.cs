using System.Diagnostics.CodeAnalysis;

namespace Yabt.FileSystem.Implementation;

internal sealed class ProspectiveFileSystemPathBackend
(
    string _rootPath,
    FileSystemPathBackend _backend,
    IEnumerable<string> _removedPaths
) : FileSystemPathBackend
{
    // This operation-local index keeps comparison and validation bounded by path depth,
    // including when many sibling names change. It is discarded after preflight.
    private readonly RemovalNode _removals = BuildRemovalTree(_rootPath, _backend, _removedPaths);

    public override StringComparison NativePathComparison => _backend.NativePathComparison;

    public override void ValidateName(string name) => _backend.ValidateName(name);

    public override StringComparer GetDirectoryNameComparer(string directoryPath) =>
        _backend.GetDirectoryNameComparer(GetSurvivingAncestor(directoryPath));

    public override bool TryGetDirectoryNameComparer
    (
        string directoryPath,
        [NotNullWhen(true)] out StringComparer? comparer
    ) => _backend.TryGetDirectoryNameComparer(GetSurvivingAncestor(directoryPath), out comparer);

    public override void ValidateExistingName
    (
        string parentPath,
        string name,
        bool allowCaseOnlyDirectoryAlias = false
    )
    {
        if (FindRemovedAncestor(Path.Combine(parentPath, name)) is not null) { return; }
        _backend.ValidateExistingName(parentPath, name, allowCaseOnlyDirectoryAlias);
    }

    private string GetSurvivingAncestor(string directoryPath)
    {
        var removedPath = FindRemovedAncestor(directoryPath);
        if (removedPath is null) { return directoryPath; }

        return Path.GetDirectoryName(removedPath) ?? throw new YabtFileSystemException
        (
            "Prospective filesystem comparison requires a surviving ancestor directory.",
            path: directoryPath
        );
    }

    private string? FindRemovedAncestor(string path)
    {
        var relativePath = Path.GetRelativePath(_rootPath, path);
        if
        (
            relativePath == "." || Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            return null;
        }

        var current = _removals;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar))
        {
            if (current.Children is null || !current.Children.TryGetValue(segment, out var child)) { return null; }
            if (child.Removed) { return child.NativePath; }
            current = child;
        }

        return null;
    }

    private static RemovalNode BuildRemovalTree
    (
        string rootPath,
        FileSystemPathBackend backend,
        IEnumerable<string> removedPaths
    )
    {
        var root = new RemovalNode(rootPath);
        foreach (var removedPath in removedPaths)
        {
            var current = root;
            foreach (var segment in Path.GetRelativePath(rootPath, removedPath).Split(Path.DirectorySeparatorChar))
            {
                if (current.Removed) { break; }
                current.Children ??= new(backend.GetDirectoryNameComparer(current.NativePath));
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new(Path.Combine(current.NativePath, segment));
                    current.Children.Add(segment, child);
                }
                current = child;
            }

            current.Removed = true;
            current.Children = null;
        }

        return root;
    }

    private sealed class RemovalNode(string nativePath)
    {
        public string NativePath { get; } = nativePath;

        public bool Removed { get; set; }

        public Dictionary<string, RemovalNode>? Children { get; set; }
    }
}
