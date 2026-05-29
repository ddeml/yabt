using Yabt.Common;

namespace Yabt.Metadata;

public class YabtMetadataException : YabtException
{
    public YabtMetadataException()
    {
    }

    public YabtMetadataException(string? message)
        : base(message)
    {
    }

    public YabtMetadataException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
