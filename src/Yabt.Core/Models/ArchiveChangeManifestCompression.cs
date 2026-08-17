namespace Yabt.Core.Models;

public static class ArchiveChangeManifestCompression
{
    public const string Brotli = "brotli";
    public const string None = "none";

    public static string GetEffective(string? configuredValue) => configuredValue ?? Brotli;

    public static bool IsSupported(string value) =>
        string.Equals(value, Brotli, StringComparison.Ordinal) ||
        string.Equals(value, None, StringComparison.Ordinal);
}
