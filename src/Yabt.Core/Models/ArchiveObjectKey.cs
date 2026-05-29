namespace Yabt.Core.Models;

public sealed record ArchiveObjectKey(ArchiveArea Area, string RelativePath)
{
    public string ToObjectPath(ArchiveLayout? layout = default)
    {
        var effectiveLayout = layout ?? ArchiveLayout.Default;
        var prefix = Area switch
        {
            ArchiveArea.Live => effectiveLayout.LivePrefix,
            ArchiveArea.Hist => effectiveLayout.HistPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(Area), Area, null),
        };

        var normalizedPath = RelativePath.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalizedPath) ?
            prefix :
            $"{prefix}/{normalizedPath}";
    }
}
