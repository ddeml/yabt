using Yabt.Common;

namespace Yabt.AzureBlob;

public class YabtAzureBlobException : YabtException
{
    public YabtAzureBlobException()
    {
    }

    public YabtAzureBlobException(string? message)
        : base(message)
    {
    }

    public YabtAzureBlobException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
