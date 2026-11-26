using System.Diagnostics;

namespace Seekbeam.Bench;

/// <summary>
/// Reproducible benchmarks (PERFORMANCE_PLAN.md).
///   synthetic &lt;dataDir&gt; &lt;count&gt; [seed]   populate a catalog with a synthetic tree then run the search benchmark
///   search &lt;dataDir&gt; [reps]                run the search benchmark on an existing data dir
/// </summary>
public static class Bench
{
    public static long PrivateMB() => Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;

    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: synthetic <dataDir> <count> [seed] | search <dataDir> [reps] | index <dataDir> <root> | content <dataDir> <root> | coverage <dataDir> [count] [seed] | folderindex <dataDir> [mode]"); return 2; }
        Seekbeam.Core.Diagnostics.Log.MinLevel = Seekbeam.Core.Diagnostics.LogLevel.Off;
        switch (args[0])
        {
            case "synthetic":
                SearchBench.Populate(args[1], long.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 42);
                SearchBench.Run(args[1], 20);
                return 0;
            case "search":
                SearchBench.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 20);
                return 0;
            case "coverage":
                CoverageBench.Run(args[1], args.Length > 2 ? long.Parse(args[2]) : 1_200_000, args.Length > 3 ? int.Parse(args[3]) : 42);
                return 0;
            case "folderindex":
                FolderIndexBench.Run(args[1], args.Length > 2 ? args[2] : "Performance");
                return 0;
            case "index":
                IndexBench.Run(args[1], args[2], args.Length > 3 ? args[3] : "Performance", args.Length > 4 && args[4] == "content");
                return 0;
            default:
                Console.WriteLine("unknown command");
                return 2;
        }
    }
}
