namespace Yabt.Common.Async;

public class YabtAsyncException : YabtException
{
    public YabtAsyncException()
    {
    }

    public YabtAsyncException(string? message) : base(message)
    {
    }

    public YabtAsyncException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

}
