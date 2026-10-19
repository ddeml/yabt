using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace Yabt.FileSystem.Implementation;

internal sealed record FileSystemFileIdentity(string Id, uint LinkCount)
{
    private const int WindowsFileIdInformationClass = 18;
    private const uint WindowsFileIdInformationSize = 24;
    private const int LinuxEmptyPath = 0x1000;
    private const uint LinuxStatxType = 0x1;
    private const uint LinuxStatxLinkCount = 0x4;
    private const uint LinuxStatxInode = 0x100;
    private const ushort LinuxFileTypeMask = 0xf000;
    private const ushort LinuxRegularFile = 0x8000;

    public static FileSystemFileIdentity Read(SafeFileHandle handle, string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { return ReadWindows(handle, path); }
            if (OperatingSystem.IsLinux()) { return ReadLinux(handle, path); }
            throw new YabtFileSystemException
            (
                "Independent filesystem object validation is not supported on this platform.", path: path
            );
        }
        catch (Exception exception)
        {
            if (exception is YabtFileSystemException) { throw; }
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

    private static FileSystemFileIdentity ReadWindows(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var information)) { throw CreateNativeException(path); }
        if
        (
            !GetFileInformationByHandleEx
            (
                handle, WindowsFileIdInformationClass, out var identity, WindowsFileIdInformationSize
            )
        )
        {
            throw CreateNativeException(path);
        }

        if
        (
            identity.FileId == Guid.Empty || information.NumberOfLinks == 0 ||
            (information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
        )
        {
            throw CreateUnsupportedEvidenceException(path);
        }

        return new($"{identity.VolumeSerialNumber:x16}:{identity.FileId:N}", information.NumberOfLinks);
    }

    private static FileSystemFileIdentity ReadLinux(SafeFileHandle handle, string path)
    {
        var addedReference = false;
        try
        {
            handle.DangerousAddRef(ref addedReference);
            var requestedInformation = LinuxStatxType | LinuxStatxLinkCount | LinuxStatxInode;
            if
            (
                Statx
                (
                    handle.DangerousGetHandle().ToInt32(), string.Empty, LinuxEmptyPath,
                    requestedInformation, out var information
                ) != 0
            )
            {
                throw CreateNativeException(path);
            }

            if
            (
                (information.Mask & requestedInformation) != requestedInformation ||
                (information.Mode & LinuxFileTypeMask) != LinuxRegularFile ||
                information.Inode == 0 || information.LinkCount == 0
            )
            {
                throw CreateUnsupportedEvidenceException(path);
            }

            return new
            (
                $"{information.DeviceMajor:x8}:{information.DeviceMinor:x8}:{information.Inode:x16}",
                information.LinkCount
            );
        }
        finally
        {
            if (addedReference) { handle.DangerousRelease(); }
        }
    }

    private static YabtFileSystemException CreateUnsupportedEvidenceException(string path) => new
    (
        $"Filesystem object '{path}' did not provide a supported regular-file identity and hard-link count.",
        path: path
    );

    private static YabtFileSystemException CreateNativeException(string path) => new
    (
        $"Filesystem object identity and hard-link count could not be read for '{path}'.",
        path: path,
        innerException: new Win32Exception(Marshal.GetLastPInvokeError())
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public FileAttributes FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileIdInformation
    {
        public ulong VolumeSerialNumber;
        public Guid FileId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatxInformation
    {
        // Linux's statx ABI has fixed-width fields and offsets on all supported architectures.
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle
    (
        SafeFileHandle file, out WindowsFileInformation information
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx
    (
        SafeFileHandle file, int informationClass, out WindowsFileIdInformation information, uint bufferSize
    );

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx
    (
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatxInformation information
    );
}
