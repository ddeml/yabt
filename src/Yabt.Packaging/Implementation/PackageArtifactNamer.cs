using System.Text;
using Yabt.Core.Models;

namespace Yabt.Packaging.Implementation;

internal static class PackageArtifactNamer
{
    public static string CreatePackageName
    (
        string sourceDirectory,
        string manifestHash,
        string extension
    )
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDirectory));
        var safeFolderName = SanitizeFileName(string.IsNullOrWhiteSpace(folderName) ? "root" : folderName);
        var fileNameHash = ToFileNameHash(manifestHash);
        var normalizedExtension = extension.Trim().TrimStart('.');

        return $"{safeFolderName}.{fileNameHash}.{normalizedExtension}";
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

    private static string ToFileNameHash(string value)
    {
        if (!ArchiveHash.IsValid(value))
        {
            throw new YabtPackagingException(
                "Package identity hash must be a canonical YABT xxHash128 hash.");
        }

        return ArchiveHash.FormatFileNameToken(value);
    }
}
