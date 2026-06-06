using Yabt.Common;

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
        string? key = default,
        Exception? innerException = default
    )
        : base(message, innerException)
    {
        Key = key;
    }

    public string? Key { get; }
}
