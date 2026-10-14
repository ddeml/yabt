namespace Yabt.FileSystem.Implementation;

internal static class FileSystemObjectName
{
    public static void Validate(string name) => FileSystemPathBackend.Current.ValidateName(name);
}
