using Microsoft.Win32.SafeHandles;
using Yabt.Common.Implementation;

namespace Yabt.Common;

/// <summary>Ephemeral identity and hard-link evidence from an opened native regular file.</summary>
public sealed record NativeFileIdentity(string Id, uint LinkCount)
{
    public static NativeFileIdentity Read(SafeFileHandle handle, string path) => NativeFileIdentityReader.Read(handle, path);

    public static void RequireIndependent(SafeFileHandle handle, string path)
    {
        var identity = Read(handle, path);
        if (identity.LinkCount == 1) { return; }
        throw new YabtException
        (
            $"Filesystem file '{path}' has {identity.LinkCount} hard links and cannot be used " +
                "as writable local control or staging storage."
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
