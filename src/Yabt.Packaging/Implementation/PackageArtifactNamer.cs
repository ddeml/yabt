using System.Text;

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
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new YabtPackagingException(
                "Package identity hash must include an algorithm and value.");
        }

        return $"{value[..separator]}-{value[(separator + 1)..]}";
    }
}
