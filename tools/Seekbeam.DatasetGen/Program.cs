using System.Diagnostics;
using Seekbeam.Tools;

// Seekbeam dataset generator (reproducible: fixed seeds).
//   tree <dir> <count> [seed] [contentPercent]   real file tree on disk (benchmarks); content files get small text bodies
//   audit <dir>                                  immutability audit dataset
//   audit-cleanup <dir>                          removes ACL deny + junctions + dataset
//   snapshot <dir> <out.json>                    reference snapshot (SHA-256, metadata, SDDL, ADS)
//   compare <before.json> <after.json>           prints differences; exit 1 on any difference except LastAccessTime
if (args.Length < 2) { Console.WriteLine("usage: tree|audit|audit-cleanup|snapshot|compare ..."); return 2; }
switch (args[0])
{
    case "tree":
    {
        string root = args[1]; long count = long.Parse(args[2]); int seed = args.Length > 3 ? int.Parse(args[3]) : 42; int pct = args.Length > 4 ? int.Parse(args[4]) : 2;
        var gen = new NameGenerator(seed);
        Directory.CreateDirectory(root);
        var dirs = new List<string>();
        var sw = Stopwatch.StartNew();
        long n = 0, bytes = 0;
        var empty = Array.Empty<byte>();
        gen.GenerateTree(count, 12,
            (parent, name, depth) =>
            {
                string p = Path.Combine(parent < 0 ? root : dirs[parent], name);
                Directory.CreateDirectory(SourceSnapshot.LongPath(p));
                dirs.Add(p);
                if (++n % 100_000 == 0) Console.WriteLine($"  {n:N0} entries, {n / sw.Elapsed.TotalSeconds:N0}/s");
                return dirs.Count - 1;
            },
            (parent, name) =>
            {
                string p = Path.Combine(parent < 0 ? root : dirs[parent], name);
                byte[] data = empty;
                if (gen.Next(100) < pct)
                {
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is ".txt" or ".md" or ".csv" or ".json" or ".log" or ".cs" or ".js" or ".html" or ".xml" or ".sql" or ".py" or ".yaml" or ".ini")
                        data = System.Text.Encoding.UTF8.GetBytes(string.Join(" ", Enumerable.Range(0, 40 + gen.Next(400)).Select(_ => gen.NextFileName())));
                }
                try { File.WriteAllBytes(SourceSnapshot.LongPath(p), data); bytes += data.Length; } catch (IOException) { }
                if (++n % 100_000 == 0) Console.WriteLine($"  {n:N0} entries, {n / sw.Elapsed.TotalSeconds:N0}/s");
            });
        Console.WriteLine($"Created {n:N0} entries ({dirs.Count:N0} directories, {bytes / 1048576.0:F1} MB of content) in {sw.Elapsed.TotalSeconds:F0} s");
        return 0;
    }
    case "audit":
    {
        var info = AuditDataset.Build(Path.GetFullPath(args[1]));
        if (args.Length > 2)
        {
            // Optional bulk tree so that a forced kill happens in the middle of indexing.
            long bulk = long.Parse(args[2]);
            var g = new NameGenerator(7);
            var bdirs = new List<string>();
            string broot = Path.Combine(info.Root, "Bulk");
            g.GenerateTree(bulk, 8, (p, n, d) => { string x = Path.Combine(p < 0 ? broot : bdirs[p], n); Directory.CreateDirectory(x); bdirs.Add(x); return bdirs.Count - 1; },
                (p, n) => { string x = Path.Combine(p < 0 ? broot : bdirs[p], Path.GetFileNameWithoutExtension(n) + (g.Next(10) == 0 ? ".txt" : Path.GetExtension(n)));
                    try { File.WriteAllText(x, x.EndsWith(".txt") ? string.Join(" ", Enumerable.Range(0, 200).Select(_ => g.NextFileName())) : ""); } catch (IOException) { } });
        }
        Console.WriteLine($"Audit dataset created at {info.Root}");
        return 0;
    }
    case "audit-cleanup":
    {
        string root = Path.GetFullPath(args[1]);
        AuditDataset.Cleanup(new AuditDataset.Info(root, Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "_outside"), Path.Combine(root, "Denied"), ""));
        return 0;
    }
    case "snapshot":
    {
        var s = SourceSnapshot.Take(Path.GetFullPath(args[1]));
        SourceSnapshot.Save(s, args[2]);
        Console.WriteLine($"{s.Count} entries snapshotted -> {args[2]}");
        return 0;
    }
    case "compare":
    {
        var diffs = SourceSnapshot.Compare(SourceSnapshot.Load(args[1]), SourceSnapshot.Load(args[2]));
        var real = diffs.Where(d => !d.IsAccessTimeOnly).ToList();
        Console.WriteLine($"Differences: {real.Count} (excluding {diffs.Count - real.Count} last-access-time updates by the OS)");
        foreach (var d in real) Console.WriteLine("  " + d);
        return real.Count == 0 ? 0 : 1;
    }
}
return 2;
