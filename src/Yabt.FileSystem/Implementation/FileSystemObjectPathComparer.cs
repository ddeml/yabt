using Yabt.Core.Models;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemObjectPathComparer
(
    string _rootPath,
    FileSystemPathBackend _backend
) : StringComparer
{
    public override int Compare(string? first, string? second)
    {
        if (ReferenceEquals(first, second)) { return 0; }
        if (first is null) { return -1; }
        if (second is null) { return 1; }
        var firstPath = ObjectPath.Parse(first);
        var secondPath = ObjectPath.Parse(second);
        if (firstPath.IsRoot || secondPath.IsRoot)
        {
            return firstPath.IsRoot ? secondPath.IsRoot ? 0 : -1 : 1;
        }

        var firstSegments = firstPath.Value.Split('/');
        var secondSegments = secondPath.Value.Split('/');
        var parentPath = _rootPath;
        var count = Math.Min(firstSegments.Length, secondSegments.Length);
        for (var index = 0; index < count; index++)
        {
            // Equal spellings need no filesystem evidence. A native path can pass through
            // an unsupported ancestor namespace before reaching a supported store mount.
            if (!string.Equals(firstSegments[index], secondSegments[index], StringComparison.Ordinal))
            {
                var nameComparer = _backend.GetDirectoryNameComparer(parentPath);
                var comparison = nameComparer.Compare(firstSegments[index], secondSegments[index]);
                if (comparison != 0) { return comparison; }
            }
            parentPath = Path.Combine(parentPath, firstSegments[index]);
        }

        return firstSegments.Length.CompareTo(secondSegments.Length);
    }

    public override bool Equals(string? first, string? second) => Compare(first, second) == 0;

    public override int GetHashCode(string value)
    {
        var path = ObjectPath.Parse(value);
        if (path.IsRoot) { return 0; }
        var hash = new HashCode();
        var parentPath = _rootPath;
        foreach (var segment in path.Value.Split('/'))
        {
            var nameComparer = _backend.GetDirectoryNameComparer(parentPath);
            hash.Add(segment, nameComparer);
            parentPath = Path.Combine(parentPath, segment);
        }

        return hash.ToHashCode();
    }
}
