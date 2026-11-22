using System.Security.Cryptography;

namespace Seekbeam.Extraction.Tests;

public class ZipTests
{
    private static byte[] Nested() =>
        Fx.Zip(("dossier/b.txt", "texte externe"), ("inner.zip", Fx.Zip(("a.txt", "contenu imbriqué"), ("deep.zip", Fx.Zip(("z.txt", "tres profond"))))));

    [Fact]
    public void NestedArchivesAreExtractedInMemory()
    {
        var r = Fx.Extract(Nested(), "zip");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Contains("dossier/b.txt", r.Text);
        Assert.Contains("texte externe", r.Text);
        Assert.Contains("inner.zip", r.Text);
        Assert.Contains("a.txt", r.Text);
        Assert.Contains("contenu imbriqué", r.Text);
        // deep.zip is at depth 2 = MaxArchiveDepth: its names are listed but its entries are not opened.
        Assert.Contains("z.txt", r.Text);
        Assert.DoesNotContain("tres profond", r.Text);
    }

    [Fact]
    public void DepthLimitIsRespected()
    {
        var r = Fx.Extract(Nested(), "zip", new ExtractionLimits { MaxArchiveDepth = 1 });
        Assert.Contains("texte externe", r.Text);
        Assert.Contains("a.txt", r.Text);
        Assert.DoesNotContain("contenu imbriqué", r.Text);

        var r0 = Fx.Extract(Nested(), "zip", new ExtractionLimits { MaxArchiveDepth = 0 });
        Assert.Contains("inner.zip", r0.Text);
        Assert.DoesNotContain("texte externe", r0.Text);
    }

    [Fact]
    public void EntryCountLimit()
    {
        var parts = Enumerable.Range(0, 10).Select(i => ($"f{i}.txt", (object)$"contenu{i}")).ToArray();
        var r = Fx.Extract(Fx.Zip(parts), "zip", new ExtractionLimits { MaxArchiveEntries = 3 });
        Assert.Equal("3", r.Metadata["entries"]);
        Assert.Equal("true", r.Metadata["entryLimitReached"]);
        Assert.Contains("contenu2", r.Text);
        Assert.DoesNotContain("contenu3", r.Text);
    }

    [Fact]
    public void CompressionRatioLimit()
    {
        var zip = Fx.Zip(("bombe.txt", new string('a', 4 * 1024 * 1024)), ("petit.txt", "petit contenu"));
        var r = Fx.Extract(zip, "zip");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Equal("1", r.Metadata["skippedEntries"]);
        Assert.Contains("petit contenu", r.Text);
        Assert.DoesNotContain("aaaaaaaa", r.Text);
    }

    [Fact]
    public void EntrySizeAndTotalBudget()
    {
        var zip = Fx.Zip(("a.txt", new string('x', 5000) + " A"), ("b.txt", "fin B"));
        var r = Fx.Extract(zip, "zip", new ExtractionLimits { MaxArchiveEntryBytes = 1000 });
        Assert.Equal("1", r.Metadata["skippedEntries"]);
        Assert.Contains("fin B", r.Text);

        var r2 = Fx.Extract(zip, "zip", new ExtractionLimits { MaxArchiveTotalBytes = 5003 });
        Assert.Contains(" A", r2.Text);
        Assert.DoesNotContain("fin B", r2.Text);
    }

    [Fact]
    public void LyingEntrySizeIsBoundedByRealBytes()
    {
        var zip = Fx.Zip(("menteur.txt", new string('m', 100_000)), ("ok.txt", "toujours lu"));
        Fx.PatchZip(zip, "menteur.txt", uncompressedSize: 10);
        var r = Fx.Extract(zip, "zip", new ExtractionLimits { MaxArchiveEntryBytes = 1000 });
        Assert.Contains("toujours lu", r.Text);
        Assert.DoesNotContain(new string('m', 2000), r.Text);
    }

    [Fact]
    public void EncryptedEntryIsSkippedWithoutFailingTheArchive()
    {
        var zip = Fx.Zip(("secret.txt", "confidentiel"), ("clair.txt", "visible"));
        Fx.PatchZip(zip, "secret.txt", orFlags: 0x0001);
        var r = Fx.Extract(zip, "zip");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Equal("1", r.Metadata["encryptedEntries"]);
        Assert.Contains("secret.txt", r.Text);
        Assert.DoesNotContain("confidentiel", r.Text);
        Assert.Contains("visible", r.Text);
    }

    [Fact]
    public void MalformedEntryDoesNotStopTheArchive()
    {
        var zip = Fx.Zip(("casse.docx", "pas un zip"), ("bon.txt", "lisible"));
        var r = Fx.Extract(zip, "zip");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Contains("lisible", r.Text);
        Assert.Equal("1", r.Metadata["failedEntries"]); // casse.docx: InvalidDataException from the OOXML extractor
    }

    [Fact]
    public void DocumentsInsideArchivesAreExtracted()
    {
        var zip = Fx.Zip(("rapport.docx", OoxmlTests.Docx("<w:p><w:r><w:t>DansLeZip</w:t></w:r></w:p>")), ("doc.pdf", PdfTests.BuildPdf("PdfDansZip")));
        var r = Fx.Extract(zip, "zip");
        Assert.Contains("DansLeZip", r.Text);
        Assert.Contains("PdfDansZip", r.Text);
    }

    [Fact]
    public void TraversalNamesAreNeverWrittenAndOnlyDisplayed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "seekbeam-zip-" + Guid.NewGuid().ToString("N"));
        string sub = Path.Combine(dir, "a", "b");
        Directory.CreateDirectory(sub);
        try
        {
            string zipPath = Path.Combine(sub, "evil.zip");
            File.WriteAllBytes(zipPath, Fx.Zip(("../evil.txt", "malveillant"), ("../../evil2.txt", "x"), ("/abs.txt", "y"), ("C:/Windows/z.txt", "z"), ("ok.txt", "sain")));
            var before = Snapshot(dir);
            var r = Fx.Registry.ExtractFile(zipPath, Fx.NoSizeLimit, CancellationToken.None);
            Assert.Equal(ExtractionStatus.Ok, r.Status);
            Assert.Contains("../evil.txt", r.Text);
            Assert.DoesNotContain("malveillant", r.Text);
            Assert.Contains("sain", r.Text);
            Assert.Equal("4", r.Metadata["unsafeEntryNames"]);
            Assert.Equal(before, Snapshot(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string Snapshot(string dir) =>
        string.Join('\n', Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order()
            .Select(p => p + "|" + (File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));

    [Fact]
    public void GarbageZipIsMalformed() => Assert.Equal(ExtractionStatus.Malformed, Fx.Extract("PK\u0003\u0004garbage"u8.ToArray(), "zip").Status);

    [Theory]
    [InlineData("ok.txt", true)]
    [InlineData("a/b/c.txt", true)]
    [InlineData("..foo/x.txt", true)]
    [InlineData("../x.txt", false)]
    [InlineData("a/../../x.txt", false)]
    [InlineData("a\\..\\x.txt", false)]
    [InlineData("/x.txt", false)]
    [InlineData("\\x.txt", false)]
    [InlineData("C:x.txt", false)]
    public void SafeNames(string name, bool safe) => Assert.Equal(safe, Formats.ZipExtractor.IsSafeName(name));
}

public class RegistryTests
{
    [Fact]
    public void UnsupportedExtension()
    {
        Assert.Equal(ExtractionStatus.Unsupported, Fx.Extract([1, 2, 3], "exe").Status);
        Assert.Null(Fx.Registry.ForExtension("jpg"));
    }

    [Fact]
    public void AllExtractorsAreRegistered()
    {
        var ids = Fx.Registry.All.Select(e => e.Id).ToHashSet();
        Assert.Equal(["text", "markup", "ooxml", "odf", "rtf", "pdf", "legacy-office", "zip"], ids);
        foreach (var ext in new[] { "txt", "cs", "html", "config", "docx", "xlsx", "pptx", "odt", "rtf", "pdf", "doc", "xls", "ppt", "zip" })
            Assert.NotNull(Fx.Registry.ForExtension(ext));
        Assert.Equal("markup", Fx.Registry.ForExtension(".config")!.Id);
        Assert.All(Fx.Registry.All, e => Assert.Equal(1, e.Version));
        Assert.All(Fx.Registry.All, e => Assert.All(e.Extensions, x => Assert.Equal(x.ToLowerInvariant(), x)));
    }

    [Fact]
    public void DisabledAndExtraExtensions()
    {
        var reg = ExtractorRegistry.CreateDefault(disabledIds: ["pdf"], extraTextExtensions: [".foo"]);
        Assert.Null(reg.ForExtension("pdf"));
        Assert.Equal("text", reg.ForExtension("foo")!.Id);
    }

    [Fact]
    public void ExtractFileLeavesTheSourceUntouched()
    {
        string dir = Path.Combine(Path.GetTempPath(), "seekbeam-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new Dictionary<string, byte[]>
            {
                ["note.txt"] = "Contenu du fichier été"u8.ToArray(),
                ["doc.docx"] = OoxmlTests.Docx("<w:p><w:r><w:t>Fichier Word</w:t></w:r></w:p>"),
                ["doc.pdf"] = PdfTests.BuildPdf("Fichier PDF"),
                ["arch.zip"] = Fx.Zip(("x.txt", "dans archive")),
            };
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            foreach (var (name, data) in files)
            {
                string p = Path.Combine(dir, name);
                File.WriteAllBytes(p, data);
                File.SetLastWriteTimeUtc(p, stamp);
                File.SetAttributes(p, FileAttributes.ReadOnly);
            }
            var before = files.Keys.ToDictionary(k => k, k => State(Path.Combine(dir, k)));
            var entriesBefore = Directory.GetFileSystemEntries(dir).Order().ToArray();

            foreach (var name in files.Keys)
            {
                var r = Fx.Registry.ExtractFile(Path.Combine(dir, name), Fx.NoSizeLimit, CancellationToken.None);
                Assert.True(r.Status == ExtractionStatus.Ok, $"{name}: {r.Status} {r.Error}");
            }
            Assert.Contains("été", Fx.Registry.ExtractFile(Path.Combine(dir, "note.txt"), Fx.NoSizeLimit, CancellationToken.None).Text);

            foreach (var name in files.Keys) Assert.Equal(before[name], State(Path.Combine(dir, name)));
            Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(dir).Order().ToArray());
        }
        finally
        {
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string State(string path) =>
        $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}|{new FileInfo(path).Length}|{File.GetAttributes(path)}|{File.GetLastWriteTimeUtc(path).Ticks}|{File.GetCreationTimeUtc(path).Ticks}";

    /// <summary>Mutation fuzzing: corrupted inputs of every format must be classified, never escape as exceptions or hang.</summary>
    [Fact]
    public void MutatedInputsNeverEscape()
    {
        var samples = new (string ext, byte[] data)[]
        {
            ("docx", OoxmlTests.Docx("<w:p><w:r><w:t>Texte</w:t></w:r></w:p>")),
            ("zip", Fx.Zip(("a.txt", "un"), ("b.zip", Fx.Zip(("c.html", "<p>deux</p>"))))),
            ("rtf", "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}caf\\'e9 \\u233?\\par}"u8.ToArray()),
            ("html", "<html><title>t</title><script>x</script><p>a&amp;b</p><![CDATA[c]]></html>"u8.ToArray()),
            ("pdf", PdfTests.BuildPdf("Fuzz")),
        };
        var rnd = new Random(1234);
        Fx.WithTimeout(() =>
        {
            foreach (var (ext, data) in samples)
                for (int i = 0; i < (ext == "pdf" ? 40 : 150); i++)
                {
                    var b = (byte[])data.Clone();
                    int flips = 1 + rnd.Next(12);
                    for (int k = 0; k < flips; k++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                    if (rnd.Next(4) == 0) b = b[..rnd.Next(b.Length)];
                    var r = Fx.Extract(b, ext);
                    Assert.NotEqual(ExtractionStatus.Error, r.Status);
                }
            return true;
        }, 60);
    }

    [Fact]
    public void ExtractFileTooLargeAndMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "seekbeam-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string p = Path.Combine(dir, "big.txt");
            File.WriteAllBytes(p, new byte[2000]);
            string small = Path.Combine(dir, "small.txt");
            File.WriteAllText(small, "petit");
            // A 5-byte file must pass a 1000-byte limit: guards the size reported by SourceFileAccess.TryGetMetadata.
            var ok = Fx.Registry.ExtractFile(small, new ExtractionLimits { MaxFileBytes = 1000 }, CancellationToken.None);
            Assert.True(ok.Status == ExtractionStatus.Ok, $"{ok.Status} (SourceFileAccess size: see WIN32_FILE_ATTRIBUTE_DATA layout)");
            Assert.Equal(ExtractionStatus.TooLarge, Fx.Registry.ExtractFile(p, new ExtractionLimits { MaxFileBytes = 1000 }, CancellationToken.None).Status);
            Assert.Equal(ExtractionStatus.Error, Fx.Registry.ExtractFile(Path.Combine(dir, "absent.txt"), new ExtractionLimits(), CancellationToken.None).Status);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
