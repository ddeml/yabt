using Yabt.Common;

namespace Yabt.Sync;

public class YabtSyncException : YabtException
{
    public YabtSyncException()
    {
    }

    public YabtSyncException(string? message)
        : base(message)
    {
    }

    public YabtSyncException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
