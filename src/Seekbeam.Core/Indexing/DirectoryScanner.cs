using Seekbeam.Core.Catalog;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Indexing;

public sealed class ScanCounters
{
    public long DirectoriesScanned, Added, Updated, Deleted, Errors, ContentQueued, EntriesSeen;
}

/// <summary>A directory child read from disk.</summary>
public readonly record struct DiskChild(string Name, FileAttributes Attributes, long Size, long CreationTime, long LastWriteTime, long FileId, uint ReparseTag)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// Synchronises one directory of the catalog with the disk (diff by name): the single mechanism behind initial scans,
/// event-driven rescans and reconciliation. Reads the disk exclusively through <see cref="SourceFileAccess"/>.
/// </summary>
public sealed class DirectoryScanner : IDisposable
{
    private readonly CatalogWriter _writer;
    private readonly DirectoryReader _reader = SourceFileAccess.CreateDirectoryReader();
    private readonly List<DiskChild> _buffer = new(256);
    public ScanCounters Counters { get; } = new();

    public DirectoryScanner(CatalogWriter writer) { _writer = writer; }

    public enum Outcome { Ok, RootUnavailable, Vanished, AccessDenied, Error }

    /// <summary>Reads a directory from disk (outside any DB transaction).</summary>
    public DirectoryReadStatus ReadDisk(string path, List<DiskChild> into, out int win32Error, CancellationToken ct)
    {
        into.Clear();
        var status = _reader.Read(path, (in RawEntry e) =>
            into.Add(new DiskChild(e.Name.ToString(), e.Attributes, e.Size, e.CreationTimeUtc, e.LastWriteTimeUtc, e.FileId, e.ReparseTag)),
            out win32Error, ct);
        return status;
    }

    /// <summary>
    /// Applies the diff between <paramref name="disk"/> and the catalog children of <paramref name="dir"/> in batch <paramref name="b"/>.
    /// </summary>
    public void ApplyDiff(CatalogWriter.Batch b, EntryInfo dir, string dirPath, RootScope root, ScopeRules scope, ContentEligibility content,
        List<DiskChild> disk, ScanMode mode, QueuePriority childPriority)
    {
        Counters.DirectoriesScanned++;
        Counters.EntriesSeen += disk.Count;
        bool depthLimited = root.Config.MaxDepth >= 0 && dir.Depth >= root.Config.MaxDepth;
        var known = new Dictionary<string, CatalogWriter.ChildRow>(StringComparer.Ordinal);
        foreach (var c in _writer.GetChildren(b, dir.Id)) known[c.Name] = c;
        int childDepth = dir.Depth + 1;
        QueuePriority contentPriority = childPriority <= QueuePriority.Discovery ? childPriority : QueuePriority.InitialScan;

        if (!depthLimited)
        {
            foreach (var d in disk)
            {
                string full = EntryStore.Combine(dirPath, d.Name);
                if (scope.IsExcluded(root, d.Name, full, d.Attributes)) continue;
                var kind = d.IsDirectory ? EntryKind.Directory : EntryKind.File;
                bool traverse = d.IsDirectory && LinkPolicy.IsTraversableDirectory(d.ReparseTag, d.Attributes);
                if (d.IsDirectory && !traverse && root.Config.FollowLinks)
                    traverse = LinkPolicy.ShouldFollow(root, scope, full, dirPath, childDepth, out _);

                if (known.Remove(d.Name, out var old))
                {
                    if (old.Kind != kind)
                    {
                        Counters.Deleted += _writer.Delete(b, old.Id, old.Kind, id => QueueContentDelete(b, content, id));
                        Insert(b, dir, root, content, d, kind, childDepth, traverse, childPriority, contentPriority, full);
                        continue;
                    }
                    bool changed = old.Size != d.Size || old.MTime != d.LastWriteTime || old.Attrs != (long)d.Attributes || old.FileId != d.FileId || old.CTime != d.CreationTime;
                    if (changed)
                    {
                        _writer.UpdateMeta(b, old, dir.Id, d.Size, d.LastWriteTime, d.CreationTime, (long)d.Attributes, d.FileId);
                        Counters.Updated++;
                        if (kind == EntryKind.File && (old.Size != d.Size || old.MTime != d.LastWriteTime) && content.IsEligible(root, full, d.Size, d.Attributes))
                        {
                            _writer.EnqueueContent(b, old.Id, contentPriority, ContentOp.Index);
                            Counters.ContentQueued++;
                        }
                    }
                    // Recursive mode visits every subdirectory; shallow mode only new ones (handled in Insert).
                    if (kind == EntryKind.Directory && traverse && mode == ScanMode.Recursive)
                        _writer.EnqueueDirectory(b, old.Id, childPriority, ScanMode.Recursive);
                }
                else Insert(b, dir, root, content, d, kind, childDepth, traverse, childPriority, contentPriority, full);
            }
        }
        // Remember directories whose content is left unexplored because of MaxDepth, so the UI can tell the user.
        int hidden = 0;
        if (depthLimited)
            foreach (var d in disk)
                if (!scope.IsExcluded(root, d.Name, EntryStore.Combine(dirPath, d.Name), d.Attributes)) hidden++;
        _writer.SetDepthLimited(b, dir.Id, dir.RootId, hidden);
        // Whatever remains in the catalog no longer exists on disk (or is now excluded / beyond max depth).
        foreach (var gone in known.Values)
            Counters.Deleted += _writer.Delete(b, gone.Id, gone.Kind, id => QueueContentDelete(b, content, id));
        _writer.MarkScanned(b, dir.Id, 0);
    }

    private void Insert(CatalogWriter.Batch b, EntryInfo dir, RootScope root, ContentEligibility content, DiskChild d, EntryKind kind, int depth,
        bool traverse, QueuePriority childPriority, QueuePriority contentPriority, string fullPath)
    {
        long id = _writer.Insert(b, dir.Id, dir.RootId, d.Name, kind, d.Size, d.LastWriteTime, d.CreationTime, (long)d.Attributes, d.FileId, depth);
        Counters.Added++;
        if (kind == EntryKind.Directory)
        {
            if (traverse) _writer.EnqueueDirectory(b, id, childPriority, ScanMode.Recursive);
        }
        else if (content.IsEligible(root, fullPath, d.Size, d.Attributes))
        {
            _writer.EnqueueContent(b, id, contentPriority, ContentOp.Index);
            Counters.ContentQueued++;
        }
    }

    private void QueueContentDelete(CatalogWriter.Batch b, ContentEligibility content, long id)
    {
        if (content.Enabled) _writer.EnqueueContent(b, id, QueuePriority.Maintenance, ContentOp.Delete);
    }

    public void Dispose() => _reader.Dispose();
}
