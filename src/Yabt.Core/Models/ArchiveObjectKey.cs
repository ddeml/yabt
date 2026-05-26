namespace Yabt.Core.Models;

public sealed record ArchiveObjectKey(ArchiveArea Area, string RelativePath)
{
    public string ToBlobName()
    {
        var prefix = Area switch
        {
            ArchiveArea.Live => "live",
            ArchiveArea.Hist => "hist",
            _ => throw new ArgumentOutOfRangeException(nameof(Area), Area, null),
        };

        var normalizedPath = RelativePath.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalizedPath) ?
            prefix :
            $"{prefix}/{normalizedPath}";
    }
}
