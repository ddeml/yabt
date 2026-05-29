using Yabt.Common;

namespace Yabt.Format.Zip;

public class YabtFormatZipException : YabtException
{
    public YabtFormatZipException()
    {
    }

    public YabtFormatZipException(string? message)
        : base(message)
    {
    }

    public YabtFormatZipException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
