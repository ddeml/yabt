namespace Yabt.Metadata;

public static class ArchiveHistoryEntryRepresentation
{
    public const string Materialized = "materialized";
    public const string Reference = "reference";

    public static bool IsSupported(string value) =>
        string.Equals(value, Materialized, StringComparison.Ordinal) ||
        string.Equals(value, Reference, StringComparison.Ordinal);
}
