using Seekbeam.Core.Catalog;
using Seekbeam.Core.Indexing;

namespace Seekbeam.Core.Content;

/// <summary>Content-index state of one file, as shown next to a search result.</summary>
public enum ContentState
{
    /// <summary>Text extracted and searchable.</summary>
    Indexed,
    /// <summary>Extracted, but the document contains no text (e.g. a scanned PDF).</summary>
    NoText,
    /// <summary>Waiting in the content queue, or changed since it was indexed.</summary>
    Pending,
    /// <summary>Content deliberately not indexed (see <see cref="ContentStateInfo.Reason"/> / <see cref="ContentStateInfo.DocStatus"/>).</summary>
    NotIndexed,
    /// <summary>Extraction failed.</summary>
    Failed,
}

/// <param name="Reason">For <see cref="ContentState.NotIndexed"/> decided by the rules (format, size, settings...).</param>
/// <param name="DocStatus">For <see cref="ContentState.NotIndexed"/> decided by the extractor (encrypted, too large, unsupported).</param>
public readonly record struct ContentStateInfo(ContentState State, ContentIneligibility Reason = ContentIneligibility.None, ContentStatus? DocStatus = null);

/// <summary>
/// Read-only lookup of the content state of a few entries (the results on screen): one primary-key read in the content
/// queue, in content.db and, when needed, in the catalog per entry. Cheap enough to run after each search.
/// </summary>
public static class ContentStateLookup
{
    public static ContentStateInfo Get(long entryId, CatalogDb catalog, ContentDb content, EntryStore entries, NameIndexer indexer, bool contentIndexerAvailable)
    {
        if (!contentIndexerAvailable) return new(ContentState.NotIndexed, ContentIneligibility.Disabled);
        var con = catalog.RentReader();
        EntryInfo? info;
        bool queued;
        try
        {
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM content_queue WHERE entry_id=$id";
                cmd.Parameters.AddWithValue("$id", entryId);
                queued = cmd.ExecuteScalar() != null;
            }
            info = EntryStore.Get(con, entryId);
        }
        finally { catalog.ReturnReader(con); }
        if (queued) return new(ContentState.Pending);
        var doc = content.GetInfo(entryId);
        return Classify(false, doc == null ? null : new DocSummary(doc.Status, doc.MTime, doc.Size), info?.MTime, info?.Size ?? 0,
            () =>
            {
                // Never extracted: either the rules exclude it, or it has not been reached yet.
                if (info == null) return null;
                string? path = entries.GetPath(entryId);
                var root = indexer.Scope.ById(info.RootId);
                if (path == null || root == null) return null;
                return indexer.Content.Check(root, path, info.Size, (FileAttributes)info.Attributes);
            });
    }

    /// <summary>What content.db knows about a document (the columns the classification needs).</summary>
    public readonly record struct DocSummary(ContentStatus Status, long MTime, long Size);

    /// <summary>
    /// The classification itself, shared by the per-result lookup and by <see cref="ContentCoverage"/> so that both
    /// always agree. <paramref name="rules"/> is only called for a file that is neither queued nor extracted; it returns
    /// null when the rules cannot be evaluated (entry or root gone), which counts as pending.
    /// </summary>
    public static ContentStateInfo Classify(bool queued, DocSummary? doc, long? entryMTime, long entrySize, Func<ContentIneligibility?> rules)
    {
        if (queued) return new(ContentState.Pending);
        if (doc is { } d)
        {
            switch (d.Status)
            {
                case ContentStatus.Indexed:
                    // Changed since extraction (the watcher or the next reconciliation will queue it).
                    return entryMTime is { } m && (m != d.MTime || entrySize != d.Size) ? new(ContentState.Pending) : new(ContentState.Indexed);
                case ContentStatus.Empty:
                    return d.Size == 0 ? new(ContentState.NotIndexed, ContentIneligibility.EmptyFile) : new(ContentState.NoText);
                case ContentStatus.Failed:
                    return new(ContentState.Failed, DocStatus: ContentStatus.Failed);
                default:
                    return new(ContentState.NotIndexed, DocStatus: d.Status);
            }
        }
        var why = rules();
        return why is null or ContentIneligibility.None ? new(ContentState.Pending) : new(ContentState.NotIndexed, why.Value);
    }
}
