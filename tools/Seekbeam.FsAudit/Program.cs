using System.Collections.Concurrent;
using System.Text;

// Seekbeam.FsAudit — independent dynamic verification (FILE_IMMUTABILITY_POLICY §7).
// Watches a directory tree with ReadDirectoryChangesW (kernel notifications) while Seekbeam runs, in two channels:
//   MODIFY : FileName | DirectoryName | Size | LastWrite | Attributes | CreationTime | Security  -> must stay empty
//   ACCESS : LastAccess only                                                                       -> OS side effect, informative
// usage: Seekbeam.FsAudit <dir> <report.txt> <stopFile>   (runs until <stopFile> exists)
if (args.Length < 3) { Console.Error.WriteLine("usage: Seekbeam.FsAudit <dir> <report> <stopFile>"); return 2; }
string dir = Path.GetFullPath(args[0]), report = Path.GetFullPath(args[1]), stop = Path.GetFullPath(args[2]);
var modify = new ConcurrentQueue<string>();
var access = new ConcurrentQueue<string>();
long overflows = 0;
FileSystemWatcher W(NotifyFilters f, ConcurrentQueue<string> q, string channel)
{
    var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = f, InternalBufferSize = 64 * 1024 };
    w.Changed += (_, e) => q.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {channel} Changed {e.FullPath}");
    w.Created += (_, e) => q.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {channel} Created {e.FullPath}");
    w.Deleted += (_, e) => q.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {channel} Deleted {e.FullPath}");
    w.Renamed += (_, e) => q.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {channel} Renamed {e.OldFullPath} -> {e.FullPath}");
    w.Error += (_, e) => { Interlocked.Increment(ref overflows); q.Enqueue($"{channel} ERROR {e.GetException().Message}"); };
    w.EnableRaisingEvents = true;
    return w;
}
using var wm = W(NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes
                 | NotifyFilters.CreationTime | NotifyFilters.Security, modify, "MODIFY");
using var wa = W(NotifyFilters.LastAccess, access, "ACCESS");
var started = DateTime.Now;
Console.WriteLine($"FsAudit watching {dir}");
File.WriteAllText(report + ".ready", "ready");
while (!File.Exists(stop)) Thread.Sleep(200);
Thread.Sleep(1000); // drain late notifications
var sb = new StringBuilder();
sb.AppendLine($"FsAudit report — {dir}");
sb.AppendLine($"Window: {started:u} .. {DateTime.Now:u}");
sb.AppendLine($"MODIFY events (names, size, write, attributes, creation, security): {modify.Count}");
sb.AppendLine($"ACCESS events (last-access time, OS side effect of reads): {access.Count}");
sb.AppendLine($"Watcher errors/overflows: {Interlocked.Read(ref overflows)}");
sb.AppendLine();
foreach (var l in modify) sb.AppendLine(l);
sb.AppendLine("--- access (first 50)");
foreach (var l in access.Take(50)) sb.AppendLine(l);
File.WriteAllText(report, sb.ToString());
Console.WriteLine(sb.ToString().Split('\n').Take(6).Aggregate((a, b) => a + "\n" + b));
return modify.IsEmpty && overflows == 0 ? 0 : 1;
