using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Seekbeam.Tools;

/// <summary>
/// Builds the immutability-audit dataset: every supported format, malformed files, archives (nested, bomb-like,
/// traversal names, encrypted flag), large files, hidden/read-only/system attributes, alternate data streams, Unicode
/// names, long paths, junctions (cycle + outside target), an ACL-denied directory, and empty files.
/// </summary>
public static class AuditDataset
{
    public sealed record Info(string Root, string OutsideTarget, string DeniedDir, string LockedFile);

    public static Info Build(string root)
    {
        Directory.CreateDirectory(root);
        void W(string rel, string text, Encoding? enc = null) { var p = P(rel); File.WriteAllText(p, text, enc ?? new UTF8Encoding(false)); }
        void B(string rel, byte[] data) => File.WriteAllBytes(P(rel), data);
        string P(string rel) { string p = Path.Combine(root, rel.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(p)!); return p; }

        W("Documents/memo.txt", "Réunion du comité : budget prévisionnel validé. Mot-clé unique: zxqauditmemo.");
        B("Documents/latin1.txt", Encoding.Latin1.GetBytes("Café crème à Genève — encodage Windows-1252"));
        W("Documents/notes.md", "# Titre\n\n* liste `code` **gras** zxqauditmarkdown");
        W("Documents/table.csv", "nom;ville\nDupont;Lausanne\nzxqauditcsv;Zürich");
        W("Documents/data.json", "{\"clé\": \"valeur zxqauditjson\", \"n\": [1,2,3]}");
        W("Documents/config.yaml", "server:\n  host: zxqaudityaml\n");
        W("Documents/page.html", "<html><head><title>Charte</title><script>alert(1)</script></head><body><p>Politique zxqaudithtml &eacute;t&eacute;</p></body></html>");
        W("Documents/data.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e; zxqauditxml</x>");
        W("Documents/notes.rtf", @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}{\*\generator Test;}\f0 Proc\'e9dure zxqauditrtf\par}");
        W("Code/Program.cs", "class P { static void Main() { /* zxqauditcode */ } }");
        W("Code/app.log", string.Concat(Enumerable.Range(0, 2000).Select(i => $"2026-11-21 10:{i % 60:00}:00 INFO request {i} ok\n")));
        B("Office/rapport.docx", Docx("Rapport annuel zxqauditdocx"));
        B("Office/tableau.xlsx", Xlsx("zxqauditxlsx"));
        B("Office/slides.pptx", Pptx("Présentation zxqauditpptx"));
        B("Office/texte.odt", Odt("Texte ODF zxqauditodt"));
        B("Office/document.pdf", Pdf("Bonjour zxqauditpdf"));
        B("Archives/bundle.zip", Zip(("readme.txt", Encoding.UTF8.GetBytes("archive zxqauditzip")), ("inner.zip", Zip(("deep.txt", Encoding.UTF8.GetBytes("nested zxqauditnested")))), ("../evil.txt", Encoding.UTF8.GetBytes("traversal name"))));
        B("Archives/bomb.zip", Zip(("zeros.txt", new byte[40 * 1024 * 1024])));
        B("Malformed/garbage.pdf", RandomBytes(5000, 1));
        B("Malformed/garbage.docx", RandomBytes(5000, 2));
        { var z = Zip(("a.txt", RandomBytes(10000, 4))); B("Malformed/truncated.zip", z[..(z.Length / 2)]); }
        B("Malformed/binary.txt", RandomBytes(20000, 3));
        B("Malformed/empty.docx", []);
        W("Malformed/empty.txt", "");
        using (var fs = new FileStream(P("Large/huge.bin.txt"), FileMode.Create)) fs.SetLength(60L * 1024 * 1024); // > 50 MB content limit
        W("Large/mid.txt", string.Concat(Enumerable.Repeat("ligne de texte assez longue zxqauditmid\n", 100_000)));
        W("Attributes/readonly.txt", "lecture seule zxqauditro");
        File.SetAttributes(P("Attributes/readonly.txt"), FileAttributes.ReadOnly);
        W("Attributes/hidden.txt", "caché zxqaudithidden");
        File.SetAttributes(P("Attributes/hidden.txt"), FileAttributes.Hidden);
        W("Attributes/system.txt", "système");
        File.SetAttributes(P("Attributes/system.txt"), FileAttributes.System);
        W("Attributes/withstream.txt", "fichier avec flux alternatif");
        File.WriteAllText(P("Attributes/withstream.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        W("Unicode/été_ñ_文件_отчёт.txt", "unicode zxqauditunicode");
        string longDir = string.Join("\\", Enumerable.Range(0, 12).Select(i => "dossier_tres_long_numero_" + i));
        Directory.CreateDirectory(SourceSnapshot.LongPath(Path.Combine(root, "LongPaths", longDir)));
        File.WriteAllText(SourceSnapshot.LongPath(Path.Combine(root, "LongPaths", longDir, "fichier_au_bout_du_chemin.txt")), "chemin long zxqauditlong");
        W("Locked/locked.txt", "verrouillé zxqauditlocked");
        W("Denied/secret.txt", "secret");
        W("Outside/placeholder.txt", "x");

        // Junctions: one creating a cycle (to the root), one to a directory outside the root.
        string outside = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "_outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside_file.txt"), "hors racine zxqauditoutside");
        Cmd($"/c mklink /J \"{P("Links/cycle")}\" \"{root}\"");
        Cmd($"/c mklink /J \"{P("Links/outside")}\" \"{outside}\"");
        // ACL: deny listing of one directory to the current user (Seekbeam must skip it, never bypass it).
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        Cmd2("icacls", $"\"{P("Denied/secret.txt")[..^11]}\" /deny \"{user}\":(RD)");
        return new Info(root, outside, Path.Combine(root, "Denied"), P("Locked/locked.txt"));
    }

    public static void Cleanup(Info info)
    {
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        Cmd2("icacls", $"\"{info.DeniedDir}\" /remove:d \"{user}\"");
        foreach (var j in new[] { "Links\\cycle", "Links\\outside" })
            try { Directory.Delete(Path.Combine(info.Root, j)); } catch { }
        try { Directory.Delete(SourceSnapshot.LongPath(info.Root), true); } catch { }
        try { Directory.Delete(info.OutsideTarget, true); } catch { }
    }

    private static void Cmd(string args) => Cmd2("cmd.exe", args);
    private static void Cmd2(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
    }

    private static byte[] RandomBytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, d) in entries)
            {
                using var s = z.CreateEntry(n, CompressionLevel.Optimal).Open();
                s.Write(d);
            }
        return ms.ToArray();
    }

    private static byte[] Docx(string text) => Zip(
        ("[Content_Types].xml", U("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")),
        ("word/document.xml", U($"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>")));

    private static byte[] Xlsx(string text) => Zip(
        ("[Content_Types].xml", U("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
        ("xl/workbook.xml", U("<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets><sheet name=\"Feuille1\" sheetId=\"1\"/></sheets></workbook>")),
        ("xl/sharedStrings.xml", U($"<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>{text}</t></si></sst>")),
        ("xl/worksheets/sheet1.xml", U("<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>42</v></c></row></sheetData></worksheet>")));

    private static byte[] Pptx(string text) => Zip(
        ("[Content_Types].xml", U("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
        ("ppt/slides/slide1.xml", U($"<?xml version=\"1.0\"?><p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>")));

    private static byte[] Odt(string text) => Zip(
        ("mimetype", U("application/vnd.oasis.opendocument.text")),
        ("content.xml", U($"<?xml version=\"1.0\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><office:text><text:p>{text}</text:p></office:text></office:body></office:document-content>")));

    private static byte[] Pdf(string text)
    {
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        return b.Build();
    }

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Search terms planted in the dataset, with whether they must be found in content.</summary>
    public static readonly string[] ContentMarkers =
        ["zxqauditmemo", "zxqauditmarkdown", "zxqauditcsv", "zxqauditjson", "zxqaudityaml", "zxqaudithtml", "zxqauditxml", "zxqauditrtf", "zxqauditcode",
         "zxqauditdocx", "zxqauditxlsx", "zxqauditpptx", "zxqauditodt", "zxqauditpdf", "zxqauditzip", "zxqauditnested", "zxqauditmid", "zxqauditro",
         "zxqauditunicode", "zxqauditlong"];
}
