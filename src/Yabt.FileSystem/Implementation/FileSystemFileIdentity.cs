using Microsoft.Win32.SafeHandles;
using Yabt.Common;

namespace Yabt.FileSystem.Implementation;

internal sealed record FileSystemFileIdentity(string Id, uint LinkCount)
{
    public static FileSystemFileIdentity Read(SafeFileHandle handle, string path)
    {
        try
        {
            var identity = NativeFileIdentity.Read(handle, path);
            return new(identity.Id, identity.LinkCount);
        }
        catch (Exception exception)
        {
            throw new YabtFileSystemException
            (
                $"Filesystem identity and hard-link evidence could not be obtained for '{path}'.",
                path: path,
                innerException: exception
            );
        }
    }

    public static void RequireIndependent(SafeFileHandle handle, string path)
    {
        var identity = Read(handle, path);
        if (identity.LinkCount == 1) { return; }
        throw new YabtFileSystemException
        (
            $"Filesystem file '{path}' has {identity.LinkCount} hard links and cannot be used " +
                "as writable archive control or staging storage.",
            path: path
        );
    }

    public static void RequireIndependentIfPresent(string path)
    {
        try
        {
            using var handle = File.OpenHandle
            (
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete
            );
            RequireIndependent(handle, path);
        }
        catch (Exception exception)
        {
            if (exception is FileNotFoundException or DirectoryNotFoundException) { return; }
            throw;
        }
    }

}
