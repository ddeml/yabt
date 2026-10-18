using System.Diagnostics.CodeAnalysis;

namespace Yabt.FileSystem.Implementation;

internal abstract class FileSystemPathBackend
{
    public static FileSystemPathBackend Current { get; } = OperatingSystem.IsWindows() ?
        new WindowsFileSystemPathBackend() : new UnixFileSystemPathBackend();

    public abstract StringComparison NativePathComparison { get; }

    public abstract void ValidateName(string name);

    public abstract StringComparer GetDirectoryNameComparer(string directoryPath);

    /// <summary>Returns false only for unsupported name semantics; access failures still throw.</summary>
    public virtual bool TryGetDirectoryNameComparer
    (
        string directoryPath,
        [NotNullWhen(true)] out StringComparer? comparer
    )
    {
        comparer = GetDirectoryNameComparer(directoryPath);
        return true;
    }

    public virtual void ValidateExistingName
    (
        string parentPath,
        string name,
        bool allowCaseOnlyDirectoryAlias = false
    )
    {
        // Proven ordinal Unix namespaces do not map an exact segment to another spelling.
    }

    protected static string FindExistingDirectory(string directoryPath)
    {
        var candidate = directoryPath;
        while (true)
        {
            var attributes = FileSystemPathSafety.TryGetAttributes(candidate);
            if (attributes.HasValue && (attributes.Value & FileAttributes.Directory) != 0)
            {
                return candidate;
            }

            candidate = Path.GetDirectoryName(candidate) ?? throw new YabtFileSystemException
            (
                "Filesystem name comparison requires an accessible existing ancestor directory.",
                path: directoryPath
            );
        }
    }

    protected static YabtFileSystemException InvalidName(string name) => new
    (
        $"Filesystem name '{name}' cannot be represented as an archive object without changing its identity.",
        key: name
    );
}
