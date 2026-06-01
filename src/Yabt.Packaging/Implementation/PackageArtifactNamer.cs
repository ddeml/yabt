using System.Text;

namespace Yabt.Packaging.Implementation;

internal static class PackageArtifactNamer
{
    public static string CreatePackageName
    (
        string sourceDirectory,
        DateTimeOffset createdAtUtc,
        string manifestHash,
        string extension
    )
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDirectory));
        var safeFolderName = SanitizeFileName(string.IsNullOrWhiteSpace(folderName) ? "root" : folderName);
        var normalizedHash = NormalizeHash(manifestHash);
        var hashPrefix = normalizedHash.Length <= 8 ? normalizedHash : normalizedHash[..8];
        var normalizedExtension = extension.Trim().TrimStart('.');

        return $"{safeFolderName}.{createdAtUtc:yyyyMMddTHHmmssZ}.{hashPrefix}.{normalizedExtension}";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private static string NormalizeHash(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? value : value[(separator + 1)..];
    }
}
