using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Tests;

public class YabtTestsException : YabtException
{
    public YabtTestsException()
    {
    }

    public YabtTestsException(string? message)
        : base(message)
    {
    }

    public YabtTestsException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public YabtTestsException
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
