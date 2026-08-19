namespace Yabt.Core.Models;

public static class ArchiveHistoryDeduplication
{
    public const long DefaultTinyFileMaximumBytes = 4096;

    public static long GetEffectiveTinyFileMaximumBytes(long? configuredValue) =>
        configuredValue ?? DefaultTinyFileMaximumBytes;

    public static bool IsSupportedTinyFileMaximumBytes(long value) => value >= 0;
}
