using Yabt.Common;

namespace Yabt.WebDav;

public class YabtWebDavException : YabtException
{
    public YabtWebDavException()
    {
    }

    public YabtWebDavException(string? message)
        : base(message)
    {
    }

    public YabtWebDavException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
