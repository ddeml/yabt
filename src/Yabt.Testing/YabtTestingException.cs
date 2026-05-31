using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Testing;

public class YabtTestingException : YabtException
{
    public YabtTestingException()
    {
    }

    public YabtTestingException(string? message)
        : base(message)
    {
    }

    public YabtTestingException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public YabtTestingException
    (
        string? message,
        ArchiveObjectKey? key = default,
        Exception? innerException = default
    )
        : base(message, innerException)
    {
        Key = key;
    }

    public ArchiveObjectKey? Key { get; }
}
