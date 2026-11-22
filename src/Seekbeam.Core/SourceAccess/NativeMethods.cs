// FILE IMMUTABILITY POLICY: P/Invoke surface used to READ source files. No write-capable entry point is declared here.
// The only CreateFileW call site is SourceNative.Open, whose access masks come from the SourceOpenKind enum (read-only).
#pragma warning disable RS0030 // SafeFileHandle is required to wrap read-only native handles.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Seekbeam.Core.SourceAccess;

internal static partial class SourceNative
{
    // Access rights (read-only subset ONLY).
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint SYNCHRONIZE = 0x00100000;

    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // READ | WRITE | DELETE: never block other applications
    private const uint OPEN_EXISTING = 3;                 // never creates anything

    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    internal const uint FILE_FLAG_OPEN_NO_RECALL = 0x00100000;   // never hydrate cloud placeholders
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    internal enum SourceOpenKind { ReadData, ListDirectory, ReadAttributes }

    /// <summary>The single CreateFileW call for source files. Access masks are read-only constants.</summary>
    internal static SafeFileHandle Open(string path, SourceOpenKind kind, bool openReparsePoint = false)
    {
        (uint access, uint flags) = kind switch
        {
            SourceOpenKind.ReadData => (GENERIC_READ, FILE_FLAG_SEQUENTIAL_SCAN | FILE_FLAG_OPEN_NO_RECALL),
            SourceOpenKind.ListDirectory => (FILE_LIST_DIRECTORY | SYNCHRONIZE, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_NO_RECALL),
            SourceOpenKind.ReadAttributes => (FILE_READ_ATTRIBUTES, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_NO_RECALL),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (openReparsePoint) flags |= FILE_FLAG_OPEN_REPARSE_POINT;
        return CreateFileW(ToNativePath(path), access, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
    }

    internal static string ToNativePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    // Handing a source to the sandboxed extractor host (TD-17): duplicate with FILE_GENERIC_READ only, verify on reception.
    internal const uint FILE_GENERIC_READ = 0x00120089; // READ_CONTROL | SYNCHRONIZE | FILE_READ_DATA | FILE_READ_EA | FILE_READ_ATTRIBUTES
    internal const uint WriteCapableRights = 0x2 /*WRITE_DATA*/ | 0x4 /*APPEND_DATA*/ | 0x10 /*WRITE_EA*/ | 0x40 /*DELETE_CHILD*/
        | 0x100 /*WRITE_ATTRIBUTES*/ | 0x10000 /*DELETE*/ | 0x40000 /*WRITE_DAC*/ | 0x80000 /*WRITE_OWNER*/ | 0x01000000 /*ACCESS_SYSTEM_SECURITY*/
        | 0x10000000 /*GENERIC_ALL*/ | 0x40000000 /*GENERIC_WRITE*/;
    internal const uint FILE_TYPE_DISK = 1;

    /// <summary>Access mask granted to an open handle (ObjectBasicInformation), or uint.MaxValue if it cannot be queried.</summary>
    internal static unsafe uint GrantedAccess(SafeHandle h)
    {
        uint* info = stackalloc uint[14]; // OBJECT_BASIC_INFORMATION: Attributes, GrantedAccess, HandleCount, PointerCount, Reserved[10]
        return NtQueryObject(h, 0, info, 56, out _) == 0 ? info[1] : uint.MaxValue;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryObject(SafeHandle handle, int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetFileType(SafeHandle handle);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateHandle(IntPtr sourceProcess, SafeHandle source, SafeHandle targetProcess, out IntPtr target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    // FILETIME is two DWORDs (4-byte aligned): Pack = 4 prevents padding after the first DWORD.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileAttributesExW(string lpFileName, int fInfoLevelId, out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_STATUS_BLOCK { public IntPtr Status; public IntPtr Information; }

    internal const int FileFullDirectoryInformation = 2;
    internal const int FileIdFullDirectoryInformation = 38;

    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtQueryDirectoryFile(SafeFileHandle fileHandle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
        out IO_STATUS_BLOCK ioStatusBlock, void* fileInformation, uint length, int fileInformationClass,
        byte returnSingleEntry, IntPtr fileName, byte restartScan);

    internal const int STATUS_SUCCESS = 0;
    internal const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006);
    internal const int STATUS_NO_SUCH_FILE = unchecked((int)0xC000000F);
    internal const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);
    internal const int STATUS_INVALID_PARAMETER = unchecked((int)0xC000000D);
    internal const int STATUS_NOT_SUPPORTED = unchecked((int)0xC00000BB);
    internal const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);

    [LibraryImport("ntdll.dll")]
    internal static partial int RtlNtStatusToDosError(int status);
}
