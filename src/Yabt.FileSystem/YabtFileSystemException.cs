using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.FileSystem;

public class YabtFileSystemException : YabtException
{
    public YabtFileSystemException()
    {
    }

    public YabtFileSystemException(string? message)
        : base(message)
    {
    }

    public YabtFileSystemException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public YabtFileSystemException
    (
        string? message,
        ArchiveObjectKey? key = default,
        string? path = default,
        Exception? innerException = default
    )
        : base(message, innerException)
    {
        Key = key;
        Path = path;
    }

    public ArchiveObjectKey? Key { get; }

    public string? Path { get; }
}
