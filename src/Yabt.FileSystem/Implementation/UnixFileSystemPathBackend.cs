using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Yabt.FileSystem.Implementation;

internal sealed class UnixFileSystemPathBackend : FileSystemPathBackend
{
    private const int OpenReadOnly = 0;
    private const int OpenCloseOnExec = 0x80000;
    private const int FileSystemInformationBufferSize = 256;
    private const long ExtFileSystem = 0xef53;
    private const long F2fsFileSystem = 0xf2f52010;
    private const long BtrfsFileSystem = 0x9123683e;
    private const long TmpfsFileSystem = 0x01021994;
    private const nuint GetInodeFlags = 0x80086601;
    private const int CaseFoldDirectoryFlag = 0x40000000;

    public override StringComparison NativePathComparison => StringComparison.Ordinal;

    public override void ValidateName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw InvalidName(name);
        }
    }

    public override StringComparer GetDirectoryNameComparer(string directoryPath) =>
        TryGetDirectoryNameComparer(directoryPath, out var comparer) ? comparer : throw UnsupportedNamespace(directoryPath);

    public override bool TryGetDirectoryNameComparer
    (
        string directoryPath,
        [NotNullWhen(true)] out StringComparer? comparer
    )
    {
        comparer = null;
        var existingPath = FindExistingDirectory(directoryPath);
        if (!OperatingSystem.IsLinux() || IntPtr.Size != sizeof(long))
        {
            return false;
        }

        var descriptor = Open(existingPath, OpenReadOnly | OpenCloseOnExec);
        if (descriptor < 0) { throw NativeFailure(existingPath); }
        try
        {
            var information = Marshal.AllocHGlobal(FileSystemInformationBufferSize);
            try
            {
                if (GetFileSystemInformation(descriptor, information) != 0) { throw NativeFailure(existingPath); }
                // Linux statfs starts with the native-word filesystem type on supported 64-bit ABIs.
                var fileSystem = Marshal.ReadInt64(information);
                if (fileSystem is ExtFileSystem or F2fsFileSystem)
                {
                    if (GetFlags(descriptor, GetInodeFlags, out var flags) != 0)
                    {
                        throw NativeFailure(existingPath);
                    }
                    if ((flags & CaseFoldDirectoryFlag) != 0) { return false; }
                }
                else if (fileSystem is not BtrfsFileSystem and not TmpfsFileSystem)
                {
                    // A mount may expose remote, casefolded, or host-translated names.
                    // Do not infer ordinal semantics merely from the Linux host OS.
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(information);
            }
        }
        finally
        {
            _ = Close(descriptor);
        }

        comparer = StringComparer.Ordinal;
        return true;
    }

    private static YabtFileSystemException UnsupportedNamespace(string path) => new
    (
        "Filesystem name comparison is unsupported or ambiguous for this directory. " +
            "Linux currently requires ext/F2FS without casefold, btrfs, or tmpfs; other Unix filesystems require explicit support.",
        path: path
    );

    private static YabtFileSystemException NativeFailure(string path) => new
    (
        "Filesystem directory name semantics could not be inspected safely.",
        path: path,
        innerException: new Win32Exception(Marshal.GetLastPInvokeError())
    );

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int GetFileSystemInformation(int descriptor, IntPtr information);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int GetFlags(int descriptor, nuint request, out int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
