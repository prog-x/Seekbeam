// FILE IMMUTABILITY POLICY: this namespace is the ONLY place allowed to write to disk.
// Every write target is validated to be inside the Seekbeam data root (never a source location).
#pragma warning disable RS0030 // Write APIs are intentionally used here (SeekbeamStorage is the write gateway).
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Seekbeam.Core.Storage;

/// <summary>Thrown when code attempts to write outside the Seekbeam data root.</summary>
public sealed class ImmutabilityViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Single gateway for every file written by Seekbeam (databases, segments, config, logs, temp).
/// All paths are relative to <see cref="DataRoot"/>; absolute paths are accepted only if they resolve inside it.
/// </summary>
public sealed class SeekbeamStorage
{
    public const string PortableMarkerFile = "portable.txt";
    public string DataRoot { get; }
    public string DbDir => Path.Combine(DataRoot, "db");
    public string IndexDir => Path.Combine(DataRoot, "index");
    public string LogDir => Path.Combine(DataRoot, "logs");
    public string TempDir => Path.Combine(DataRoot, "tmp");
    public string CacheDir => Path.Combine(DataRoot, "cache");
    /// <summary>True when running in portable mode (data next to the executable).</summary>
    public bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFile));

    private static int s_sqliteInit;

    public SeekbeamStorage(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("dataRoot");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        Directory.CreateDirectory(full);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new ImmutabilityViolationException($"Seekbeam data root must not be a reparse point: {full}");
        DataRoot = full;
        foreach (var d in new[] { DbDir, IndexDir, LogDir, TempDir, CacheDir })
            Directory.CreateDirectory(d);
    }

    /// <summary>Default data root: %LOCALAPPDATA%\Seekbeam, or &lt;exe&gt;\Data in portable mode.</summary>
    public static string ResolveDefaultDataRoot(string? exeDir = null)
    {
        exeDir ??= AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, PortableMarkerFile)))
            return Path.Combine(exeDir, "Data");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seekbeam");
    }

    /// <summary>Validates and returns the absolute path for a Seekbeam-owned file.</summary>
    public string Resolve(string path)
    {
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(DataRoot, path));
        if (!IsUnderDataRoot(full))
            throw Violation($"Write target outside Seekbeam data root: {full}");
        // Refuse reparse points between DataRoot and target (a junction could redirect writes to a source).
        string? dir = Path.GetDirectoryName(full);
        while (dir != null && dir.Length > DataRoot.Length)
        {
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw Violation($"Reparse point inside Seekbeam data root: {dir}");
            dir = Path.GetDirectoryName(dir);
        }
        return full;
    }

    public bool IsUnderDataRoot(string fullPath)
    {
        string f = Path.GetFullPath(fullPath);
        return f.Length > DataRoot.Length
            && f.StartsWith(DataRoot, StringComparison.OrdinalIgnoreCase)
            && (f[DataRoot.Length] == '\\' || f[DataRoot.Length] == '/');
    }

    private static ImmutabilityViolationException Violation(string msg)
    {
        Diagnostics.Log.Error("Storage", msg);
        return new ImmutabilityViolationException(msg);
    }

    // ---------------------------------------------------------------- files

    public bool Exists(string relative) => File.Exists(Resolve(relative));

    public string? ReadAllText(string relative)
    {
        string p = Resolve(relative);
        return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
    }

    /// <summary>Atomic replace: write to tmp then File.Replace/Move (crash-safe).</summary>
    public void WriteAllTextAtomic(string relative, string content) =>
        WriteAllBytesAtomic(relative, new UTF8Encoding(false).GetBytes(content));

    public void WriteAllBytesAtomic(string relative, byte[] content)
    {
        string target = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tmp = Resolve(Path.Combine(TempDir, Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp"));
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(content);
            fs.Flush(true);
        }
        // Antivirus / Windows Search may briefly hold the previous file open: retry with backoff (sharing violations are transient).
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(target)) File.Replace(tmp, target, null, ignoreMetadataErrors: true);
                else File.Move(tmp, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 12)
                {
                    try { File.Move(tmp, target, overwrite: true); return; }
                    catch (Exception) { TryDelete(tmp); throw; }
                }
                Thread.Sleep(Math.Min(1000, 20 << attempt));
            }
        }
    }

    /// <summary>Creates a new Seekbeam-owned file for sequential writing (fails if it exists).</summary>
    public Stream CreateNew(string relative, int bufferSize = 1 << 16)
    {
        string p = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return new FileStream(p, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize, FileOptions.SequentialScan);
    }

    /// <summary>Flushes a stream obtained from this class through to the disk.</summary>
    public static void FlushDurable(Stream s)
    {
        if (s is FileStream f) f.Flush(flushToDisk: true);
        else s.Flush();
    }

    /// <summary>Opens a Seekbeam-owned file for appending (logs).</summary>
    public Stream OpenAppend(string relative)
    {
        string p = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096);
    }

    public Stream OpenReadOwn(string relative)
    {
        string p = Resolve(relative);
        return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
    }

    public bool TryDelete(string relative)
    {
        try
        {
            string p = Resolve(relative);
            if (!File.Exists(p)) return true;
            File.Delete(p);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Move(string relativeFrom, string relativeTo, bool overwrite)
        => File.Move(Resolve(relativeFrom), Resolve(relativeTo), overwrite);

    public long FileSize(string relative)
    {
        string p = Resolve(relative);
        return File.Exists(p) ? new FileInfo(p).Length : 0;
    }

    public IReadOnlyList<string> ListFiles(string relativeDir, string pattern)
    {
        string d = Resolve(Path.Combine(relativeDir, "_")); // validate directory location
        d = Path.GetDirectoryName(d)!;
        return Directory.Exists(d) ? Directory.GetFiles(d, pattern).Select(f => Path.GetFileName(f)).ToList() : [];
    }

    public long DirectorySize(string relativeDir)
    {
        string d = Path.GetDirectoryName(Resolve(Path.Combine(relativeDir, "_")))!;
        if (!Directory.Exists(d)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch (IOException) { }
        }
        return total;
    }

    /// <summary>Deletes every file in tmp\ (Seekbeam-owned). Called at startup.</summary>
    public void CleanTemp()
    {
        foreach (var f in Directory.EnumerateFiles(TempDir))
        {
            try { File.Delete(Resolve(f)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes every Seekbeam-owned index file (used by "reset index" and --purge-data).</summary>
    public void DeleteAllOwnedData(bool includeConfig)
    {
        foreach (var dir in new[] { DbDir, IndexDir, TempDir, CacheDir, LogDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.Delete(Resolve(f)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (includeConfig) TryDelete("config.json");
    }

    // ---------------------------------------------------------------- SQLite

    /// <summary>Opens (creating if needed) a Seekbeam database located in db\.</summary>
    public SqliteConnection OpenDatabase(string fileName, bool readOnly = false)
    {
        EnsureSqliteInitialized();
        string p = Resolve(Path.Combine(DbDir, fileName));
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = p,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var con = new SqliteConnection(csb.ToString());
        con.Open();
        return con;
    }

    public string DatabasePath(string fileName) => Resolve(Path.Combine(DbDir, fileName));

    private void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref s_sqliteInit, 1) == 1) return;
        SQLitePCL.Batteries_V2.Init();
        // Keep any SQLite temporary file inside Seekbeam's own tmp directory (never %TEMP% or a source).
        try { SQLitePCL.raw.sqlite3_win32_set_directory(2 /* SQLITE_WIN32_TEMP_DIRECTORY_TYPE */, TempDir); }
        catch (Exception ex) { Diagnostics.Log.Warn("Storage", "sqlite3_win32_set_directory failed: " + ex.Message); }
    }

    // ---------------------------------------------------------------- memory-mapped segments

    /// <summary>Maps a Seekbeam-owned file read-only.</summary>
    public MappedFile MapReadOnly(string relative) => new(Resolve(relative));
}

/// <summary>Read-only memory mapping of a Seekbeam-owned file (segment). Dispose unmaps.</summary>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _ptr;
    public long Length { get; }
    public string Path { get; }

    internal MappedFile(string fullPath)
    {
        Path = fullPath;
        var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess);
        Length = fs.Length;
        _mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _ptr = p + _view.PointerOffset;
    }

    public byte* Pointer => _ptr;

    public void Dispose()
    {
        if (_ptr == null) return;
        _ptr = null;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
