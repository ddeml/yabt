namespace Yabt.Sync.Implementation;

internal sealed class RestoreItemReadException : YabtSyncException
{
    public RestoreItemReadException(string relativePath, Exception innerException)
        : base($"Archive object for restore path '{relativePath}' could not be read.", innerException)
    {
        RelativePath = relativePath;
    }

    public RestoreItemReadException(string relativePath, string message)
        : base(message)
    {
        RelativePath = relativePath;
    }

    public string? RelativePath { get; }
}
