namespace Yabt.Common;

public sealed class TemporaryContentOptions
{
    public const string DefaultConfigurationSectionPath = "TemporaryContent";

    public int? MemoryThresholdBytes { get; init; }

    public long? TotalMemoryBudgetBytes { get; init; }

    public string? TemporaryDirectory { get; init; }

    public int GetEffectiveMemoryThresholdBytes()
    {
        var value = MemoryThresholdBytes ?? 8 * 1024 * 1024;
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MemoryThresholdBytes));
        return value;
    }

    public long GetEffectiveTotalMemoryBudgetBytes()
    {
        var value = TotalMemoryBudgetBytes ?? 64L * 1024 * 1024;
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(TotalMemoryBudgetBytes));
        return value;
    }

    public string GetEffectiveTemporaryDirectory() =>
        TemporaryDirectory is null ? Path.GetTempPath() : Path.GetFullPath(TemporaryDirectory);
}
