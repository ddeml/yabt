namespace Yabt.Common;

public class YabtException : Exception
{
    public YabtException()
    {
    }

    public YabtException(string? message)
        : base(message)
    {
    }

    public YabtException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
