namespace Seekbeam.Immutability.Tests;

/// <summary>Proves the IL analyzer actually detects violations (it must not pass vacuously).</summary>
public class AnalyzerSelfTests
{
    // Never executed: only compiled so that its IL can be inspected.
    internal static class Offender
    {
        public static void DeleteSomething(string p) => File.Delete(p);
        public static void Lambda(string p) => Task.Run(() => File.SetLastWriteTimeUtc(p, DateTime.UtcNow));
        public static async Task AsyncWrite(string p) { await Task.Yield(); await File.WriteAllTextAsync(p, "x"); }
        public static void Writer(string p) { using var w = new StreamWriter(p); }
        public static void Info(string p) => new FileInfo(p).Attributes = FileAttributes.ReadOnly;
    }

    [Fact]
    public void DetectsDirectLambdaAsyncCtorAndPropertyWrites()
    {
        var r = IlAnalyzer.Analyze(typeof(AnalyzerSelfTests).Assembly.Location);
        var flagged = r.Calls.Where(StaticAnalysisTests.IsWriteApi).Where(c => c.CallerType.Contains("Offender")).Select(c => c.CalleeType + "::" + c.CalleeMember).ToHashSet();
        Assert.Contains("System.IO.File::Delete", flagged);
        Assert.Contains("System.IO.File::SetLastWriteTimeUtc", flagged);   // inside a lambda (compiler-generated closure type)
        Assert.Contains("System.IO.File::WriteAllTextAsync", flagged);     // inside an async state machine
        Assert.Contains("System.IO.StreamWriter::.ctor", flagged);
        Assert.Contains("System.IO.FileSystemInfo::set_Attributes", flagged);
    }
}
