using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yabt.FileSystem.Implementation;

internal sealed record FileSystemDirectoryIdentity
(
    string Id,
    string PhysicalPath,
    string DeviceId,
    ulong? MountId = null
)
{
    private const uint WindowsFileFlagBackupSemantics = 0x02000000;
    private const int WindowsFileIdInformationClass = 18;
    private const uint WindowsFileIdInformationSize = 24;
    private const int LinuxCurrentWorkingDirectory = -100;
    private const uint LinuxStatxInode = 0x100;
    private const uint LinuxStatxMountId = 0x1000;

    public static FileSystemDirectoryIdentity Read(string path)
    {
        if (OperatingSystem.IsWindows()) { return ReadWindows(path); }
        if (OperatingSystem.IsLinux()) { return ReadLinux(path); }
        throw new YabtFileSystemException
        (
            "Approved source directory traversal requires filesystem identity support on this platform.",
            path: path
        );
    }

    public static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var attributes = FileSystemPathSafety.TryGetAttributes(fullPath);
        if (attributes.HasValue)
        {
            return Read(fullPath).PhysicalPath;
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null) { return fullPath; }
        return Path.Combine(ResolvePhysicalPath(parent), Path.GetFileName(fullPath));
    }

    private static FileSystemDirectoryIdentity ReadWindows(string path)
    {
        using var handle = CreateFileW
        (
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            WindowsFileFlagBackupSemantics,
            IntPtr.Zero
        );
        if (handle.IsInvalid) { throw CreateNativeException(path); }
        if
        (
            !GetFileInformationByHandleEx
            (
                handle,
                WindowsFileIdInformationClass,
                out var information,
                WindowsFileIdInformationSize
            )
        )
        {
            throw CreateNativeException(path);
        }
        if (information.FileId == Guid.Empty)
        {
            throw new YabtFileSystemException
            (
                "Approved source directory links require a supported nonzero physical file identity.", path: path
            );
        }

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) { throw CreateNativeException(path); }
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) { throw CreateNativeException(path); }
        }

        return new
        (
            $"{information.VolumeSerialNumber:x16}:{information.FileId:N}",
            NormalizeWindowsPhysicalPath(buffer.ToString()),
            $"{information.VolumeSerialNumber:x16}"
        );
    }

    internal static string NormalizeWindowsPhysicalPath(string physicalPath)
    {
        if
        (
            physicalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase) ||
            physicalPath.StartsWith("\\\\", StringComparison.Ordinal) &&
                !physicalPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
        )
        {
            throw new YabtFileSystemException
            (
                "Approved source directory-link traversal with UNC storage is not supported because " +
                "share aliases can hide overlapping archive directories.", path: physicalPath
            );
        }

        // Keep volume-GUID paths rooted; only the extended DOS-drive prefix is optional.
        return physicalPath.StartsWith("\\\\?\\", StringComparison.Ordinal) &&
            physicalPath.Length > 6 && physicalPath[5] == ':' ?
            physicalPath[4..] : physicalPath;
    }

    private static FileSystemDirectoryIdentity ReadLinux(string path)
    {
        var requestedInformation = LinuxStatxInode | LinuxStatxMountId;
        if (Statx(LinuxCurrentWorkingDirectory, path, 0, requestedInformation, out var information) != 0)
        {
            throw CreateNativeException(path);
        }
        if ((information.Mask & requestedInformation) != requestedInformation)
        {
            throw new YabtFileSystemException
            (
                "Approved source directory links require Linux statx inode and mount identity support (Linux 5.8 or later).",
                path: path
            );
        }

        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) { throw CreateNativeException(path); }
        try
        {
            var physicalPath = Marshal.PtrToStringUTF8(resolved) ??
                throw new YabtFileSystemException("Filesystem returned an empty physical path.", path: path);
            return new
            (
                $"{information.DeviceMajor:x8}:{information.DeviceMinor:x8}:{information.Inode:x16}",
                physicalPath,
                $"{information.DeviceMajor:x8}:{information.DeviceMinor:x8}",
                information.MountId
            );
        }
        finally
        {
            Free(resolved);
        }
    }

    private static YabtFileSystemException CreateNativeException(string path) => new
    (
        $"Physical filesystem identity could not be read for '{path}'.",
        path: path,
        innerException: new Win32Exception(Marshal.GetLastPInvokeError())
    );

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
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong MountId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW
    (
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx
    (
        SafeFileHandle file,
        int informationClass,
        out WindowsFileIdInformation information,
        uint bufferSize
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW
    (
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags
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

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
