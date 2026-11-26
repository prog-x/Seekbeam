using Seekbeam.Core.SourceAccess;
using Seekbeam.Core.Text;

namespace Seekbeam.Core.Search;

/// <summary>
/// Live completion of an absolute path typed in the launcher ("C:\Win", "\\server\", "%APPDATA%\Mic"), like the address
/// bar of Explorer. The parent folder is listed from the disk (read-only enumeration through <see cref="SourceFileAccess"/>,
/// never the index), so it also works outside the indexed locations. Folders come first, then names starting with what
/// was typed, then names containing it.
/// </summary>
public static class PathCompleter
{
    /// <summary>At most this many directory entries are read per keystroke (huge folders are cut, not slowed down).</summary>
    public static int MaxEntriesRead { get; internal set; } = 20_000;

    /// <summary>True for "C:\…", "C:/…", "\\server…" and "%VAR%…".</summary>
    public static bool IsAbsolutePathQuery(string? query)
    {
        string q = (query ?? "").Trim().Trim('"');
        if (q.Length >= 3 && char.IsAsciiLetter(q[0]) && q[1] == ':' && q[2] is '\\' or '/') return true;
        if (q.StartsWith(@"\\", StringComparison.Ordinal) && q.Length >= 3) return true;
        return q.Length >= 3 && q[0] == '%' && q.IndexOf('%', 1) > 1;
    }

    /// <summary>Splits a typed path into the folder to list and the beginning of a name ("C:\Win" → "C:\", "Win").</summary>
    public static (string Folder, string Prefix)? Split(string query)
    {
        string q = Environment.ExpandEnvironmentVariables(query.Trim().Trim('"')).Replace('/', '\\');
        if (q.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server, \\server\, \\server\sha: the shares of the server are listed (Folder = "\\server").
            string rest = q[2..];
            int sep = rest.IndexOf('\\');
            if (sep < 0) return rest.Length > 0 ? (@"\\" + rest, "") : null;
            string server = rest[..sep], after = rest[(sep + 1)..];
            if (!after.Contains('\\')) return (@"\\" + server, after);
        }
        if (q.Length < 3 || q.Contains('%')) return null;
        int last = q.LastIndexOf('\\');
        if (last < 0) return null;
        string folder = q[..(last + 1)], prefix = q[(last + 1)..];
        if (folder.Length > 3) folder = folder.TrimEnd('\\'); // "C:\Users\" -> "C:\Users", but keep "C:\"
        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return (folder, prefix);
    }

    public static SearchResultSet Complete(string query, int max, bool includeHidden, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (Split(query) is not var (folder, prefix)) return SearchResultSet.Empty;
        var found = new List<(string Name, bool Dir, long Size, long MTime, int Rank)>();
        byte[] norm = Normalizer.NormalizeToBytes(prefix);
        bool isServer = folder.StartsWith(@"\\", StringComparison.Ordinal) && folder.IndexOf('\\', 2) < 0;
        if (isServer)
        {
            foreach (var share in NetShares.List(folder))
                if (Rank(share, prefix, norm) is int r) found.Add((share, true, 0, 0, r));
        }
        else
        {
            using var reader = SourceFileAccess.CreateDirectoryReader();
            // The enumeration really stops at the limit (the reader checks the token between batches of entries).
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int seen = 0;
            try
            {
                reader.Read(folder, (in RawEntry e) =>
                {
                    if (++seen > MaxEntriesRead) { limit.Cancel(); return; }
                    var a = e.Attributes;
                    if (!includeHidden && (a & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
                    string name = e.Name.ToString();
                    if (Rank(name, prefix, norm) is int r) found.Add((name, (a & FileAttributes.Directory) != 0, e.Size, e.LastWriteTimeUtc, r));
                }, out _, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // limit reached: keep what was read
        }
        ct.ThrowIfCancellationRequested();
        // Folders first, then "starts with" before "contains", then alphabetical.
        found.Sort((x, y) => x.Dir != y.Dir ? (x.Dir ? -1 : 1) : x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank) : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
        var results = new List<SearchResult>(Math.Min(max, found.Count));
        foreach (var f in found.Take(max))
        {
            string full = folder.EndsWith('\\') ? folder + f.Name : folder + "\\" + f.Name;
            results.Add(new SearchResult
            {
                Kind = f.Dir ? ResultKind.Folder : ResultKind.File, Origin = ResultOrigin.Disk, Name = f.Name, FullPath = full, Directory = folder,
                Size = f.Size, ModifiedUtc = f.MTime > 0 ? DateTime.FromFileTimeUtc(f.MTime) : DateTime.MinValue,
                Score = 1000 - results.Count,
                Highlights = prefix.Length == 0 ? [] : SearchEngine.ComputeHighlights(f.Name, [norm]),
            });
        }
        return new SearchResultSet { Results = results, TotalMatches = found.Count, Elapsed = sw.Elapsed };
    }

    /// <summary>0 = name starts with the prefix, 1 = contains it (accent/case-insensitive), null = no match.</summary>
    private static int? Rank(string name, string prefix, byte[] norm)
    {
        if (prefix.Length == 0) return 0;
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        var n = Normalizer.NormalizeToBytes(name);
        int at = n.AsSpan().IndexOf(norm);
        return at == 0 ? 0 : at > 0 ? 1 : null;
    }
}
