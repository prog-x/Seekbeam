using Seekbeam.Core.Search;

namespace Seekbeam.Core.Tests;

/// <summary>Placement of the content results in the launcher list (<see cref="LauncherMerge"/>).</summary>
public sealed class LauncherMergeTests
{
    private static SearchResult R(long id, bool fuzzy = false) => new() { EntryId = id, Name = "n" + id, FullPath = @"C:\x\n" + id, Fuzzy = fuzzy };
    private static List<SearchResult> Names(int n, long from = 1) => Enumerable.Range(0, n).Select(i => R(from + i)).ToList();
    private static List<long> Ids(List<SearchResult> l) => l.Select(r => r.EntryId).ToList();

    [Fact]
    public void ContentFillsTheFreeRowsOfAPartialList()
    {
        var merged = LauncherMerge.Merge(Names(3), Names(10, 100), 8, out int added);
        Assert.Equal(5, added);
        Assert.Equal([1, 2, 3, 100, 101, 102, 103, 104], Ids(merged));
    }

    [Fact]
    public void AFullListKeepsReservedRowsForContentInsteadOfAppendingBelowTheFold()
    {
        var merged = LauncherMerge.Merge(Names(8), Names(5, 100), 8, out int added);
        Assert.Equal(2, added);
        Assert.Equal(8, merged.Count); // never longer than the list
        Assert.Equal([1, 2, 3, 4, 5, 6, 100, 101], Ids(merged));
    }

    [Fact]
    public void ContentRanksAboveApproximateNameMatches()
    {
        var names = new List<SearchResult> { R(1), R(2, fuzzy: true), R(3, fuzzy: true) };
        var merged = LauncherMerge.Merge(names, [R(100)], 8, out _);
        Assert.Equal([1, 100, 2, 3], Ids(merged));
    }

    [Fact]
    public void DocumentsAlreadyShownByNameAreNotRepeated()
    {
        var merged = LauncherMerge.Merge(Names(2), [R(2), R(100)], 8, out int added);
        Assert.Equal(1, added);
        Assert.Equal([1, 2, 100], Ids(merged));
    }

    [Fact]
    public void NoContentLeavesTheNameResultsUntouched()
    {
        var names = Names(8);
        var merged = LauncherMerge.Merge(names, [], 8, out int added);
        Assert.Equal(0, added);
        Assert.Equal(Ids(names), Ids(merged));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(8, 2)]
    [InlineData(50, 2)]
    public void ReservedRowsScaleWithTheListSize(int max, int reserved) => Assert.Equal(reserved, LauncherMerge.ReservedRows(max));

    [Fact]
    public void ContentRowsRequestedMatchFreeRowsOrTheReservedShare()
    {
        Assert.Equal(8, LauncherMerge.ContentRows(0, 8));
        Assert.Equal(5, LauncherMerge.ContentRows(3, 8));
        Assert.Equal(2, LauncherMerge.ContentRows(8, 8));
        Assert.Equal(0, LauncherMerge.ContentRows(2, 2));
    }
}
