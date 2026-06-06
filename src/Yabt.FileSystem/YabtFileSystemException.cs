using Yabt.Common;

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
        string? key = default,
        string? path = default,
        Exception? innerException = default
    )
        : base(message, innerException)
    {
        Key = key;
        Path = path;
    }

    public string? Key { get; }

    public string? Path { get; }
}
