using Yabt.Common;

namespace Yabt.Core;

public class YabtCoreException : YabtException
{
    public YabtCoreException()
    {
    }

    public YabtCoreException(string? message)
        : base(message)
    {
    }

    public YabtCoreException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
