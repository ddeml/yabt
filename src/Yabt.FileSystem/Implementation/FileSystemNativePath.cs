namespace Yabt.FileSystem.Implementation;

internal static class FileSystemNativePath
{
    public static void ValidateSpelling(string nativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(nativePath);
        if (!OperatingSystem.IsWindows() && nativePath.Contains('\\'))
        {
            throw new YabtFileSystemException
            (
                $"Native filesystem path '{nativePath}' contains a literal backslash and cannot " +
                    "be represented as an archive path without changing its identity.",
                path: nativePath
            );
        }

        var rootLength = Path.GetPathRoot(nativePath)?.Length ?? 0;
        var segments = nativePath[rootLength..].Split
        (
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        );
        foreach (var segment in segments)
        {
            if (segment is "." or "..") { continue; }

            FileSystemObjectName.Validate(segment);
        }
    }
}
