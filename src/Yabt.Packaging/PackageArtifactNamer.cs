using System.Text;
using Yabt.Core.Models;

namespace Yabt.Packaging;

public static class PackageArtifactNamer
{
    public static string CreatePackageName
    (
        string sourceDirectory,
        DateTimeOffset createdAtUtc,
        string manifestHash,
        ArchiveFormat format
    )
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDirectory));
        var safeFolderName = SanitizeFileName(string.IsNullOrWhiteSpace(folderName) ? "root" : folderName);
        var hashPrefix = manifestHash.Length <= 8 ? manifestHash : manifestHash[..8];
        var extension = format switch
        {
            ArchiveFormat.SevenZip => "7z",
            ArchiveFormat.TarGzip => "tar.gz",
            ArchiveFormat.Zip => "zip",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        return $"{safeFolderName}.{createdAtUtc:yyyyMMddTHHmmssZ}.{hashPrefix}.{extension}";
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
}
