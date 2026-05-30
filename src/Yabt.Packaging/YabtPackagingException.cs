using Yabt.Common;

namespace Yabt.Packaging;

public class YabtPackagingException : YabtException
{
    public YabtPackagingException()
    {
    }

    public YabtPackagingException(string? message)
        : base(message)
    {
    }

    public YabtPackagingException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
