using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace Yabt.FileSystem.Implementation;

internal sealed class WindowsFileSystemPathBackend : FileSystemPathBackend
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileCaseSensitiveInformationClass = 23;
    private const uint FileCaseSensitiveInformationSize = sizeof(uint);
    private const uint CaseSensitiveDirectoryFlag = 0x1;
    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly FrozenSet<string> ReservedNames = new[]
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public override StringComparison NativePathComparison => StringComparison.OrdinalIgnoreCase;

    public override void ValidateName(string name)
    {
        if
        (
            name.Length == 0 || name is "." or ".." ||
            name.IndexOfAny(InvalidNameCharacters) >= 0 || name.Any(character => character < ' ') ||
            name.EndsWith(' ') || name.EndsWith('.') || ReservedNames.Contains(name.Split('.')[0])
        )
        {
            throw InvalidName(name);
        }
    }

    public override StringComparer GetDirectoryNameComparer(string directoryPath)
    {
        var existingPath = FindExistingDirectory(directoryPath);
        using var handle = CreateFileW
        (
            existingPath,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero
        );
        if
        (
            handle.IsInvalid || !GetFileInformationByHandleEx
            (
                handle,
                FileCaseSensitiveInformationClass,
                out var information,
                FileCaseSensitiveInformationSize
            )
        )
        {
            throw new YabtFileSystemException
            (
                "Filesystem directory case sensitivity could not be determined; refusing an ambiguous destination namespace.",
                path: existingPath,
                innerException: new Win32Exception(Marshal.GetLastPInvokeError())
            );
        }

        return (information.Flags & CaseSensitiveDirectoryFlag) != 0 ?
            StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    }

    public override void ValidateExistingName
    (
        string parentPath,
        string name,
        bool allowCaseOnlyDirectoryAlias = false
    )
    {
        var parentAttributes = FileSystemPathSafety.TryGetAttributes(parentPath);
        if (!parentAttributes.HasValue || (parentAttributes.Value & FileAttributes.Directory) == 0) { return; }
        var path = Path.Combine(parentPath, name);
        if (!FileSystemPathSafety.TryGetAttributes(path).HasValue) { return; }

        // Ask the filesystem for its actual spelling. Managed wildcard enumeration can use a
        // different Unicode case table, and short names can also resolve to a different long name.
        var handle = FindFirstFileW(path, out var information);
        if (handle == new IntPtr(-1))
        {
            throw new YabtFileSystemException
            (
                "The existing filesystem name could not be inspected safely.",
                path: path,
                innerException: new Win32Exception(Marshal.GetLastPInvokeError())
            );
        }
        try
        {
            if
            (
                allowCaseOnlyDirectoryAlias &&
                (information.Attributes & (uint)FileAttributes.Directory) != 0 &&
                string.Equals(name, information.FileName, StringComparison.OrdinalIgnoreCase)
            )
            {
                return;
            }

            if (!string.Equals(name, information.FileName, StringComparison.Ordinal))
            {
                throw new YabtFileSystemException
                (
                    $"Object name '{name}' resolves to existing filesystem name '{information.FileName}' with a different spelling.",
                    key: name,
                    path: path
                );
            }
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindFileInformation
    {
        public uint Attributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindFirstFileW(string fileName, out FindFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

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
        out FileCaseSensitiveInformation information,
        uint bufferSize
    );
}
