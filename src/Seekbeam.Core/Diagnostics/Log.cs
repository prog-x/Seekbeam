using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Diagnostics;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3, Off = 9 }

/// <summary>
/// Minimal asynchronous rolling file logger. Privacy: callers must never log search queries or document content.
/// Paths are only logged in warnings/errors and can be redacted (config Logging.RedactPaths).
/// </summary>
public static class Log
{
    private static Channel<string>? s_channel;
    private static SeekbeamStorage? s_storage;
    private static Task? s_writer;
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static bool RedactPaths { get; set; }
    public static long MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    public static int MaxFiles { get; set; } = 5;
    /// <summary>Last errors kept in memory for the dashboard (bounded).</summary>
    public static readonly ConcurrentQueue<string> RecentErrors = new();

    public static void Initialize(SeekbeamStorage storage, LogLevel level)
    {
        s_storage = storage;
        MinLevel = level;
        if (s_channel != null) return;
        s_channel = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        s_writer = Task.Run(WriterLoop);
    }

    public static void Debug(string component, string message) => Write(LogLevel.Debug, component, message);
    public static void Info(string component, string message) => Write(LogLevel.Info, component, message);
    public static void Warn(string component, string message) => Write(LogLevel.Warn, component, message);
    public static void Error(string component, string message, Exception? ex = null) =>
        Write(LogLevel.Error, component, ex == null ? message : message + " | " + ex.GetType().Name + ": " + ex.Message);

    public static string P(string? path) => path == null ? "" : RedactPaths ? "<path#" + (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) + ">" : path;

    private static void Write(LogLevel level, string component, string message)
    {
        if (level < MinLevel) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{Environment.CurrentManagedThreadId,3}] {component}: {message}";
        if (level >= LogLevel.Warn)
        {
            RecentErrors.Enqueue(line);
            while (RecentErrors.Count > 200) RecentErrors.TryDequeue(out _);
        }
        s_channel?.Writer.TryWrite(line);
    }

    public static void Flush(TimeSpan timeout)
    {
        var ch = s_channel;
        if (ch == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (ch.Reader.Count > 0 && sw.Elapsed < timeout) Thread.Sleep(10);
    }

    private static async Task WriterLoop()
    {
        var reader = s_channel!.Reader;
        Stream? stream = null;
        string? current = null;
        var sb = new StringBuilder();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            sb.Clear();
            while (reader.TryRead(out var line) && sb.Length < 256 * 1024) sb.Append(line).Append("\r\n");
            try
            {
                string name = $"seekbeam-{DateTime.Now:yyyyMMdd}.log";
                string rel = Path.Combine("logs", name);
                if (current != name || stream == null || stream.Length > MaxFileBytes)
                {
                    stream?.Dispose();
                    if (current == name && s_storage!.FileSize(rel) > MaxFileBytes)
                        s_storage.Move(rel, Path.Combine("logs", $"seekbeam-{DateTime.Now:yyyyMMdd-HHmmss}.log"), overwrite: true);
                    stream = s_storage!.OpenAppend(rel);
                    current = name;
                    Prune();
                }
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(bytes);
                stream.Flush();
            }
            catch (Exception)
            {
                stream?.Dispose();
                stream = null;
            }
        }
    }

    private static void Prune()
    {
        var files = s_storage!.ListFiles("logs", "seekbeam-*.log").OrderByDescending(f => f, StringComparer.Ordinal).ToList();
        foreach (var f in files.Skip(MaxFiles)) s_storage.TryDelete(Path.Combine("logs", f));
    }

    public static void PurgeLogs()
    {
        if (s_storage == null) return;
        foreach (var f in s_storage.ListFiles("logs", "*.log")) s_storage.TryDelete(Path.Combine("logs", f));
    }
}
