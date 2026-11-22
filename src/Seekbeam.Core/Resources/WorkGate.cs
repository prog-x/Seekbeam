namespace Seekbeam.Core.Resources;

public enum WorkClass { Names = 0, Content = 1, Maintenance = 2 }

/// <summary>
/// Admission control for background work. Workers call <see cref="WaitTurn"/> before each batch and
/// <see cref="Report"/> after it; the gate applies pause, throttling and search-priority yielding.
/// </summary>
public interface IWorkGate
{
    /// <summary>Blocks while the work class is paused or over budget. Throws on cancellation.</summary>
    void WaitTurn(WorkClass cls, CancellationToken ct);
    /// <summary>Reports processed items/bytes (feeds the rate limiter).</summary>
    void Report(WorkClass cls, int items, long bytes);
    /// <summary>Suggested number of items per batch for this class.</summary>
    int BatchSize(WorkClass cls);
}

/// <summary>Gate without limits (tests, benchmarks).</summary>
public sealed class UnthrottledGate : IWorkGate
{
    public static readonly UnthrottledGate Instance = new();
    public void WaitTurn(WorkClass cls, CancellationToken ct) => ct.ThrowIfCancellationRequested();
    public void Report(WorkClass cls, int items, long bytes) { }
    public int BatchSize(WorkClass cls) => cls == WorkClass.Content ? 8 : 64;
}
