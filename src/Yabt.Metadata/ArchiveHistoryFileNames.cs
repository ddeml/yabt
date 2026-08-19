using System.Globalization;
using Yabt.Core.Models;

namespace Yabt.Metadata;

public static class ArchiveHistoryFileNames
{
    public const string Manifest = ".yabt-history-manifest.json";
    public const string ReferenceSuffix = ArchiveHistoryContentReference.ReferenceFileNameSuffix;

    public static string CreateReferencePath(string originalRelativePath)
    {
        var normalizedPath = ArchiveLayout.NormalizeObjectKey(originalRelativePath);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            throw new ArgumentException(
                "A history reference requires a nonempty original relative path.",
                nameof(originalRelativePath));
        }

        return $"{normalizedPath}{ReferenceSuffix}";
    }

    public static bool IsReferencePathFor
    (
        string originalRelativePath,
        string storedRelativePath
    )
    {
        var normalizedOriginalPath = ArchiveLayout.NormalizeObjectKey(originalRelativePath);
        var normalizedStoredPath = ArchiveLayout.NormalizeObjectKey(storedRelativePath);
        if (string.IsNullOrEmpty(normalizedOriginalPath) ||
            string.IsNullOrEmpty(normalizedStoredPath))
        {
            return false;
        }

        if (string.Equals(
                normalizedStoredPath,
                $"{normalizedOriginalPath}{ReferenceSuffix}",
                StringComparison.Ordinal))
        {
            return true;
        }

        var sequencePrefix = $"{normalizedOriginalPath}.";
        if (!normalizedStoredPath.StartsWith(sequencePrefix, StringComparison.Ordinal) ||
            !normalizedStoredPath.EndsWith(ReferenceSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var sequenceLength = normalizedStoredPath.Length -
            sequencePrefix.Length -
            ReferenceSuffix.Length;
        if (sequenceLength <= 0)
        {
            return false;
        }

        var sequenceText = normalizedStoredPath.Substring(sequencePrefix.Length, sequenceLength);
        return int.TryParse(
                sequenceText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence) &&
            sequence > 0 &&
            string.Equals(
                sequenceText,
                sequence.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }
}
