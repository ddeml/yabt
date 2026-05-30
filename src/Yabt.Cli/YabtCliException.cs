using Yabt.Common;

namespace Yabt.Cli;

public class YabtCliException : YabtException
{
    public YabtCliException()
    {
    }

    public YabtCliException(string? message)
        : base(message)
    {
    }

    public YabtCliException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
