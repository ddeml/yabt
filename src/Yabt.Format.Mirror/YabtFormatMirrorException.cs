using Yabt.Common;

namespace Yabt.Format.Mirror;

public class YabtFormatMirrorException : YabtException
{
    public YabtFormatMirrorException()
    {
    }

    public YabtFormatMirrorException(string? message)
        : base(message)
    {
    }

    public YabtFormatMirrorException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
