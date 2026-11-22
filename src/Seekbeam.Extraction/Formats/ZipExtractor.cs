using System.Globalization;
using System.IO.Compression;

namespace Seekbeam.Extraction.Formats;

/// <summary>
/// ZIP archives: entry names are always indexed; supported entries are decompressed in memory (never to disk) under
/// per-entry, per-archive and shared budgets, then dispatched to their extractor. One bad entry never fails the archive.
/// </summary>
internal sealed class ZipExtractor : IContentExtractor
{
    public string Id => "zip";
    public int Version => 1;
    public IReadOnlyCollection<string> Extensions { get; } = ["zip"];

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct)
    {
        var limits = context.Limits;
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        // Entry contents are only extracted while the nesting depth allows it; deeper archives only contribute names.
        bool descend = context.ArchiveDepth < limits.MaxArchiveDepth;
        int entries = 0, encrypted = 0, skipped = 0, failed = 0, unsafeNames = 0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (sink.IsFull) break;
            if (Interlocked.Increment(ref context.Budget.EntriesUsed) > limits.MaxArchiveEntries)
            {
                sink.SetMetadata("entryLimitReached", "true");
                break;
            }
            entries++;
            string name = entry.FullName;
            sink.Append(name);
            sink.Break();
            if (name.EndsWith('/') || name.EndsWith('\\')) continue; // directory entry

            if (entry.IsEncrypted) { encrypted++; continue; }
            if (!descend) continue;
            if (!IsSafeName(name)) { unsafeNames++; continue; } // traversal / absolute names: display only

            string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0 || context.Registry.ForExtension(ext) is not { } extractor) continue;

            // Guards evaluated on declared sizes BEFORE decompressing anything; the real byte count is enforced while reading.
            long remaining = limits.MaxArchiveTotalBytes - Interlocked.Read(ref context.Budget.BytesUsed);
            long cap = Math.Min(limits.MaxArchiveEntryBytes, remaining);
            if (entry.Length > cap
                || (entry.Length > ZipPackage.RatioCheckThreshold && entry.Length > (long)limits.MaxCompressionRatio * Math.Max(1, entry.CompressedLength)))
            {
                skipped++;
                continue;
            }

            MemoryStream? ms = null;
            try
            {
                ms = BoundedReadStream.ToMemory(entry.Open(), entry.Length, cap, name, ct);
                Interlocked.Add(ref context.Budget.BytesUsed, ms.Length);
                extractor.Extract(ms, ext, sink, context.Nested(), ct);
                sink.Break();
            }
            catch (OperationCanceledException) { throw; }
            catch (ExtractionException e) when (e.Status == ExtractionStatus.Encrypted) { encrypted++; }
            catch (ExtractionException) { skipped++; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed++; // malformed entry (InvalidDataException, XmlException, …): keep going with the next one
            }
            finally { ms?.Dispose(); }
        }

        sink.SetMetadata("entries", entries.ToString(CultureInfo.InvariantCulture));
        if (encrypted > 0) sink.SetMetadata("encryptedEntries", encrypted.ToString(CultureInfo.InvariantCulture));
        if (skipped > 0) sink.SetMetadata("skippedEntries", skipped.ToString(CultureInfo.InvariantCulture));
        if (failed > 0) sink.SetMetadata("failedEntries", failed.ToString(CultureInfo.InvariantCulture));
        if (unsafeNames > 0) sink.SetMetadata("unsafeEntryNames", unsafeNames.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Rejects names with '..' segments, rooted paths, drive letters or NULs (they are only ever displayed).</summary>
    internal static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name.Contains('\0') || name.Contains(':')) return false;
        if (name[0] is '/' or '\\') return false;
        foreach (var seg in name.Split('/', '\\'))
            if (seg == "..") return false;
        return true;
    }
}
