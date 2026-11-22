// FILE IMMUTABILITY POLICY: the ONLY gateway used to access indexed (source) files.
// Exposes read operations exclusively. Handles are opened with read-only access masks, OPEN_EXISTING,
// full sharing (never blocks other apps) and FILE_FLAG_OPEN_NO_RECALL (never hydrates cloud placeholders).
#pragma warning disable RS0030 // FileStream/SafeFileHandle wrap handles opened read-only by SourceNative.Open.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Seekbeam.Core.SourceAccess;

public sealed class SourceAccessException(string message, int win32Error) : IOException(message)
{
    public int Win32Error { get; } = win32Error;
    public bool IsAccessDenied => Win32Error is 5;
    public bool IsNotFound => Win32Error is 2 or 3;
    public bool IsSharingViolation => Win32Error is 32 or 33;
}

public readonly record struct SourceMetadata(FileAttributes Attributes, long Size, long CreationTimeUtc, long LastWriteTimeUtc, long LastAccessTimeUtc)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
    /// <summary>Reading this file would trigger a cloud recall (hydration) — forbidden.</summary>
    public bool IsRecallOnAccess => (((uint)Attributes) & (SourceFileAccess.RecallOnDataAccess | SourceFileAccess.RecallOnOpen | (uint)FileAttributes.Offline)) != 0;
}

public readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex, uint LinkCount);

public static class SourceFileAccess
{
    public const uint RecallOnOpen = 0x00040000;
    public const uint RecallOnDataAccess = 0x00400000;

    /// <summary>Opens a source file for reading only. Throws <see cref="SourceAccessException"/>.</summary>
    public static ReadOnlySourceStream OpenRead(string path, bool allowRecall = false, Action<int>? onRead = null)
    {
        if (!TryGetMetadata(path, out var md))
            throw new SourceAccessException("Source not found: " + path, Marshal.GetLastPInvokeError());
        if (md.IsDirectory) throw new SourceAccessException("Source is a directory: " + path, 5);
        if (!allowRecall && md.IsRecallOnAccess)
            throw new SourceAccessException("Cloud placeholder not hydrated (reading would trigger a download): " + path, 0x187 /*ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING-ish*/);
        SafeFileHandle h = SourceNative.Open(path, SourceNative.SourceOpenKind.ReadData);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new SourceAccessException($"Cannot open source (error {err}): {path}", err);
        }
        var fs = new FileStream(h, FileAccess.Read, bufferSize: 0, isAsync: false);
        return new ReadOnlySourceStream(fs, onRead);
    }

    public static bool TryGetMetadata(string path, out SourceMetadata metadata)
    {
        if (SourceNative.GetFileAttributesExW(SourceNative.ToNativePath(path), 0, out var d))
        {
            metadata = new SourceMetadata((FileAttributes)d.dwFileAttributes, ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow,
                d.ftCreationTime, d.ftLastWriteTime, d.ftLastAccessTime);
            return true;
        }
        metadata = default;
        return false;
    }

    public static bool FileExists(string path) => TryGetMetadata(path, out var m) && !m.IsDirectory;
    public static bool DirectoryExists(string path) => TryGetMetadata(path, out var m) && m.IsDirectory;

    /// <summary>Volume serial + file index (stable NTFS identity), used for cycle detection.</summary>
    public static bool TryGetIdentity(string path, out FileIdentity id)
    {
        using var h = SourceNative.Open(path, SourceNative.SourceOpenKind.ReadAttributes);
        if (h.IsInvalid || !SourceNative.GetFileInformationByHandle(h, out var info)) { id = default; return false; }
        id = new FileIdentity(info.dwVolumeSerialNumber, ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow, info.nNumberOfLinks);
        return true;
    }

    /// <summary>Resolves the final (link-free) path of an existing file or directory.</summary>
    public static unsafe string? TryGetFinalPath(string path)
    {
        using var h = SourceNative.Open(path, SourceNative.SourceOpenKind.ReadAttributes);
        if (h.IsInvalid) return null;
        char* buf = stackalloc char[1024];
        uint n = SourceNative.GetFinalPathNameByHandleW(h, buf, 1024, 0);
        if (n == 0 || n >= 1024) return null;
        string s = new(buf, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) return s[4..];
        return s;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes of a small source file (e.g. .lnk).</summary>
    public static byte[] ReadAllBytesBounded(string path, int maxBytes)
    {
        using var s = OpenRead(path);
        long len = Math.Min(s.Length, maxBytes);
        var buf = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = s.Read(buf, read, (int)len - read);
            if (n <= 0) break;
            read += n;
        }
        return read == len ? buf : buf[..read];
    }

    public static DirectoryReader CreateDirectoryReader() => new();

    /// <summary>
    /// Duplicates the handle of an opened source into <paramref name="targetProcess"/> (the sandboxed extractor host, TD-17)
    /// with an access mask reduced to FILE_GENERIC_READ: the receiving process can read this one file and nothing else,
    /// and the kernel refuses any write, delete, attribute, timestamp or ACL change through it. Returns the handle value
    /// valid in the target process (closed by the target, or automatically when it exits).
    /// </summary>
    internal static long DuplicateReadHandleInto(ReadOnlySourceStream source, SafeHandle targetProcess)
    {
        if (!SourceNative.DuplicateHandle(SourceNative.GetCurrentProcess(), source.Handle, targetProcess, out IntPtr remote,
                SourceNative.FILE_GENERIC_READ, false, 0))
        {
            int err = Marshal.GetLastPInvokeError();
            throw new SourceAccessException($"Cannot hand the source to the extractor (error {err})", err);
        }
        return remote.ToInt64();
    }

    /// <summary>
    /// Wraps a source handle received from Seekbeam (extractor host side). Refuses anything that is not a disk file opened
    /// read-only: if the handle's granted access contains any write-capable right, it is closed and an exception is thrown.
    /// </summary>
    public static ReadOnlySourceStream FromReceivedHandle(long value)
    {
        var h = new SafeFileHandle((IntPtr)value, ownsHandle: true);
        if (h.IsInvalid) { h.Dispose(); throw new SourceAccessException("Invalid source handle", 6); }
        uint granted = SourceNative.GrantedAccess(h);
        if (granted == uint.MaxValue || (granted & SourceNative.WriteCapableRights) != 0 || SourceNative.GetFileType(h) != SourceNative.FILE_TYPE_DISK)
        {
            h.Dispose();
            throw new SourceAccessException($"Refused source handle (access 0x{granted:X})", 5);
        }
        return new ReadOnlySourceStream(new FileStream(h, FileAccess.Read, bufferSize: 0, isAsync: false), null);
    }
}

/// <summary>Read-only stream over a source file. Every write member throws.</summary>
public sealed class ReadOnlySourceStream : Stream
{
    private readonly FileStream _inner;
    private readonly Action<int>? _onRead;
    internal ReadOnlySourceStream(FileStream inner, Action<int>? onRead) { _inner = inner; _onRead = onRead; }
    internal SafeFileHandle Handle => _inner.SafeFileHandle;

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));
    public override int ReadByte() { int b = _inner.ReadByte(); if (b >= 0) _onRead?.Invoke(1); return b; }
    private int Count(int n) { if (n > 0) _onRead?.Invoke(n); return n; }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    private const string ReadOnlyMessage = "Source files are read-only (FILE_IMMUTABILITY_POLICY).";
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException(ReadOnlyMessage);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(ReadOnlyMessage);
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(ReadOnlyMessage);
    public override void WriteByte(byte value) => throw new NotSupportedException(ReadOnlyMessage);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new NotSupportedException(ReadOnlyMessage);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => throw new NotSupportedException(ReadOnlyMessage);
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw new NotSupportedException(ReadOnlyMessage);

    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}

public enum DirectoryReadStatus { Ok, NotFound, AccessDenied, NotADirectory, Error }

/// <summary>One directory child as returned by NtQueryDirectoryFile. <see cref="Name"/> is only valid during the callback.</summary>
public readonly ref struct RawEntry
{
    public readonly ReadOnlySpan<char> Name;
    public readonly FileAttributes Attributes;
    public readonly long Size;
    public readonly long CreationTimeUtc;
    public readonly long LastWriteTimeUtc;
    public readonly long FileId;
    public readonly uint ReparseTag;
    public RawEntry(ReadOnlySpan<char> name, FileAttributes a, long size, long ctime, long mtime, long fileId, uint tag)
    { Name = name; Attributes = a; Size = size; CreationTimeUtc = ctime; LastWriteTimeUtc = mtime; FileId = fileId; ReparseTag = tag; }
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
}

public delegate void RawEntryVisitor(in RawEntry entry);

/// <summary>
/// Streaming directory enumerator built on NtQueryDirectoryFile (FileIdFullDirectoryInformation): name, size, times,
/// attributes, FileId and reparse tag in one system call per 64 KB batch, no per-entry allocation.
/// Not thread-safe: use one reader per thread.
/// </summary>
public sealed unsafe class DirectoryReader : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly byte* _buffer;
    private bool _disposed;

    internal DirectoryReader() { _buffer = (byte*)NativeMemory.AlignedAlloc(BufferSize, 8); }

    public DirectoryReadStatus Read(string directoryPath, RawEntryVisitor visitor, out int win32Error, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        win32Error = 0;
        using var h = SourceNative.Open(directoryPath, SourceNative.SourceOpenKind.ListDirectory);
        if (h.IsInvalid)
        {
            win32Error = Marshal.GetLastPInvokeError();
            return win32Error switch { 2 or 3 => DirectoryReadStatus.NotFound, 5 => DirectoryReadStatus.AccessDenied, 267 => DirectoryReadStatus.NotADirectory, _ => DirectoryReadStatus.Error };
        }
        int infoClass = SourceNative.FileIdFullDirectoryInformation;
        bool restart = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int status = SourceNative.NtQueryDirectoryFile(h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _, _buffer, BufferSize,
                infoClass, 0, IntPtr.Zero, restart ? (byte)1 : (byte)0);
            if (status is SourceNative.STATUS_INVALID_INFO_CLASS or SourceNative.STATUS_NOT_SUPPORTED or SourceNative.STATUS_INVALID_PARAMETER
                && infoClass == SourceNative.FileIdFullDirectoryInformation)
            {
                infoClass = SourceNative.FileFullDirectoryInformation; // e.g. some network / FAT file systems
                restart = true;
                continue;
            }
            restart = false;
            if (status == SourceNative.STATUS_NO_MORE_FILES || status == SourceNative.STATUS_NO_SUCH_FILE) return DirectoryReadStatus.Ok;
            if (status < 0)
            {
                win32Error = SourceNative.RtlNtStatusToDosError(status);
                return status == SourceNative.STATUS_ACCESS_DENIED ? DirectoryReadStatus.AccessDenied : DirectoryReadStatus.Error;
            }
            byte* p = _buffer;
            while (true)
            {
                uint next = *(uint*)p;
                long ctime = *(long*)(p + 8);
                long mtime = *(long*)(p + 24);
                long size = *(long*)(p + 40);
                uint attrs = *(uint*)(p + 56);
                int nameBytes = *(int*)(p + 60);
                uint ea = *(uint*)(p + 64);
                long fileId; char* name;
                if (infoClass == SourceNative.FileIdFullDirectoryInformation) { fileId = *(long*)(p + 72); name = (char*)(p + 80); }
                else { fileId = 0; name = (char*)(p + 68); }
                var span = new ReadOnlySpan<char>(name, nameBytes / 2);
                if (!(span.Length == 1 && span[0] == '.') && !(span.Length == 2 && span[0] == '.' && span[1] == '.'))
                {
                    var fa = (FileAttributes)attrs;
                    uint tag = (fa & FileAttributes.ReparsePoint) != 0 ? ea : 0;
                    visitor(new RawEntry(span, fa, (fa & FileAttributes.Directory) != 0 ? 0 : size, ctime, mtime, fileId, tag));
                }
                if (next == 0) break;
                p += next;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.AlignedFree(_buffer);
    }
}

public static class ReparseTags
{
    public const uint MountPoint = 0xA0000003; // junctions and volume mount points
    public const uint Symlink = 0xA000000C;
    public const uint AppExecLink = 0x8000001B;
    public const uint WciLink = 0x90001018;

    /// <summary>Cloud files (OneDrive etc.): IO_REPARSE_TAG_CLOUD_* = 0x9000x01A.</summary>
    public static bool IsCloud(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;
    /// <summary>Name-surrogate tags redirect to another location (links): bit 29.</summary>
    public static bool IsNameSurrogate(uint tag) => (tag & 0x20000000) != 0;
}
