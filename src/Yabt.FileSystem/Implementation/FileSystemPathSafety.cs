namespace Yabt.FileSystem.Implementation;

internal static class FileSystemPathSafety
{
    public static void RejectDescendantLinks(string rootPath, string path)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        if (relativePath == ".") { return; }
        if
        (
            Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new YabtFileSystemException("Filesystem path is outside the configured root.", path: path);
        }

        var currentPath = rootPath;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            var attributes = TryGetAttributes(currentPath);
            if (!attributes.HasValue) { return; }
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                throw new YabtFileSystemException
                (
                    $"Filesystem path '{currentPath}' is a link or reparse point below the configured root. " +
                    "Only explicitly approved source directory links can be followed.",
                    path: currentPath
                );
            }
        }
    }

    public static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
            throw new YabtFileSystemException
            (
                $"Filesystem attributes could not be read for '{path}'.", path: path, innerException: ex
            );
        }
    }
}
