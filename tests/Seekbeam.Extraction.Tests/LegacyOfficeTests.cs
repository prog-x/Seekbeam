using System.Text;

namespace Seekbeam.Extraction.Tests;

public class LegacyOfficeTests
{
    // ------------------------------------------------------------------ DOC

    private static (byte[] wordDocument, byte[] table) DocStreams(ushort flags, string piece1, string piece2, int size)
    {
        var p2 = Encoding.Latin1.GetBytes(piece2);
        var w = new Bin()
            .U16(0xA5EC).U16(0x00C1).U16(0).U16(0x040C).U16(0).U16(flags).U16(0x00BF).U32(0).U8(0).U8(0).U16(0).U16(0).U32(0).U32(0)
            .PadTo(32)
            .U16(14).Zeros(28)                             // csw + FibRgW97
            .U16(22);                                      // cslw
        var lw = new byte[88];
        BitConverter.GetBytes(piece1.Length + p2.Length).CopyTo(lw, 3 * 4); // ccpText
        w.Bytes(lw).U16(93);                               // FibRgLw97 + cbRgFcLcb
        var fcLcb = new byte[93 * 8];
        var clx = new Bin()
            .U8(0x01).U16(2).U16(0xABCD)                   // one Prc (skipped)
            .U8(0x02).U32(4 * 3 + 8 * 2)                   // Pcdt: lcb of PlcPcd for 2 pieces
            .U32(0).U32((uint)piece1.Length).U32((uint)(piece1.Length + p2.Length))
            .U16(0).U32(0x400).U16(0)                                   // piece 1: UTF-16LE at fc 0x400
            .U16(0).U32((0x600u * 2) | 0x40000000u).U16(0)              // piece 2: compressed (8-bit) at 0x600
            .ToArray();
        BitConverter.GetBytes(0).CopyTo(fcLcb, 33 * 8);            // fcClx
        BitConverter.GetBytes(clx.Length).CopyTo(fcLcb, 33 * 8 + 4); // lcbClx
        w.Bytes(fcLcb).PadTo(0x400).Utf16(piece1).PadTo(0x600).Bytes(p2).PadTo(size);
        return (w.ToArray(), clx);
    }

    private static byte[] Doc(ushort flags = 0x0200, int size = 0x700)
    {
        var (wd, table) = DocStreams(flags, "Bonjour ", "café \u0013HYPERLINK \"x\"\u0014lien\u0015 fin\r", size);
        // Decoy 0Table: fWhichTblStm selects 1Table.
        return CfbWriter.Build([("WordDocument", wd), ("0Table", new byte[64]), ("1Table", table)]);
    }

    [Theory]
    [InlineData(0x700)]   // WordDocument in the mini stream
    [InlineData(6000)]    // WordDocument in regular sectors
    public void DocPieceTableUnicodeAndCompressedPieces(int size)
    {
        var r = Fx.Extract(Doc(size: size), "doc");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Equal("Bonjour café lien fin", r.Text);
        Assert.Equal("legacy-office", r.ExtractorId);
    }

    [Fact]
    public void EncryptedDoc() => Assert.Equal(ExtractionStatus.Encrypted, Fx.Extract(Doc(flags: 0x0300), "doc").Status);

    [Fact]
    public void MisnamedDocIsDetectedFromStreams() => Assert.Contains("Bonjour", Fx.Extract(Doc(), "xls").Text);

    // ------------------------------------------------------------------ XLS

    private static Bin Rec(Bin b, int type, byte[] body) => b.U16(type).U16(body.Length).Bytes(body);

    private static byte[] Workbook(bool encrypted = false)
    {
        var b = new Bin();
        Rec(b, 0x0809, new Bin().U16(0x0600).U16(0x0005).Zeros(12).ToArray());
        if (encrypted) Rec(b, 0x002F, new byte[54]);
        Rec(b, 0x0085, new Bin().U32(0).U8(0).U8(0).U8(6).U8(0).Latin1("Feuil1").ToArray());
        // SST: "Alpha", "Bonjour monde" split after "Bonjour " (continued as UTF-16 with a fresh option byte), rich "Zeta".
        Rec(b, 0x00FC, new Bin().U32(3).U32(3).U16(5).U8(0).Latin1("Alpha").U16(13).U8(0).Latin1("Bonjour ").ToArray());
        Rec(b, 0x003C, new Bin().U8(0x01).Utf16("monde").U16(4).U8(0x08).U16(1).Latin1("Zeta").U32(0x00010000).ToArray());
        Rec(b, 0x000A, []);
        Rec(b, 0x0809, new Bin().U16(0x0600).U16(0x0010).Zeros(12).ToArray());
        Rec(b, 0x00FD, new Bin().U16(0).U16(0).U16(0).U32(0).ToArray());
        Rec(b, 0x00FD, new Bin().U16(0).U16(1).U16(0).U32(1).ToArray());
        Rec(b, 0x0203, new Bin().U16(1).U16(0).U16(0).F64(42.5).ToArray());
        Rec(b, 0x00FD, new Bin().U16(1).U16(1).U16(0).U32(2).ToArray());
        Rec(b, 0x027E, new Bin().U16(2).U16(0).U16(0).U32((7u << 2) | 2).ToArray());
        Rec(b, 0x0204, new Bin().U16(3).U16(0).U16(0).U16(5).U8(0).Latin1("Label").ToArray());
        Rec(b, 0x00FD, new Bin().U16(3).U16(1).U16(0).U32(9999).ToArray()); // out-of-range SST index: ignored
        Rec(b, 0x000A, []);
        b.U16(0x0203).U16(500); // truncated trailing record
        return CfbWriter.Build([("Workbook", b.ToArray())]);
    }

    [Fact]
    public void XlsSstAcrossContinueAndCells()
    {
        var r = Fx.Extract(Workbook(), "xls");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Contains("Feuil1", r.Text);
        Assert.Contains("Alpha Bonjour monde", r.Text);
        Assert.Contains("42.5 Zeta", r.Text);
        Assert.Contains("\n7", r.Text);
        Assert.Contains("Label", r.Text);
        Assert.Equal("Feuil1", r.Metadata["sheets"]);
    }

    [Fact]
    public void EncryptedXls() => Assert.Equal(ExtractionStatus.Encrypted, Fx.Extract(Workbook(encrypted: true), "xls").Status);

    [Theory]
    [InlineData(0x00000001u, 0.0)]          // fX100 on 0.0
    [InlineData((7u << 2) | 2, 7.0)]
    [InlineData((12345u << 2) | 3, 123.45)]
    [InlineData(0x3FF00000u, 1.0)]          // high 30 bits of IEEE 1.0
    public void RkDecoding(uint rk, double expected) => Assert.Equal(expected, Formats.XlsReader.DecodeRk(rk), 9);

    // ------------------------------------------------------------------ PPT

    private static byte[] Ppt()
    {
        var chars = Encoding.Unicode.GetBytes("Diapo été");
        var bytes = Encoding.Latin1.GetBytes("Notes ok");
        var nested = new Bin().U16(0).U16(0x0FA8).U32(0xFFFFFFF0).Latin1("Coupe").ToArray(); // bogus length, clamped
        var children = new Bin()
            .U16(0).U16(0x0FA0).U32((uint)chars.Length).Bytes(chars)
            .U16(0).U16(0x0FA8).U32((uint)bytes.Length).Bytes(bytes)
            .U16(0).U16(0x0FBA).U32(4).Utf16("xx")                         // CString: ignored
            .U16(0x000F).U16(0x0FF0).U32((uint)nested.Length).Bytes(nested)
            .ToArray();
        var doc = new Bin()
            .U16(0x000F).U16(0x03E8).U32((uint)children.Length).Bytes(children)
            .U16(0).U16(0x0FA8).U32(3).Latin1("Fin")
            .ToArray();
        return CfbWriter.Build([("PowerPoint Document", doc), ("Current User", new byte[20])]);
    }

    [Fact]
    public void PptTextAtomsInContainers()
    {
        var r = Fx.Extract(Ppt(), "ppt");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Equal("Diapo été\nNotes ok\nCoupe\nFin", r.Text);
    }

    [Fact]
    public void EncryptedPpt()
    {
        var cfb = CfbWriter.Build([("PowerPoint Document", new byte[16]), ("EncryptedSummary", new byte[16])]);
        Assert.Equal(ExtractionStatus.Encrypted, Fx.Extract(cfb, "ppt").Status);
    }

    // ------------------------------------------------------------------ CFB robustness

    [Fact]
    public void FatLoopIsMalformedQuickly()
    {
        var (wd, table) = DocStreams(0x0200, "x", "y", 0x700);
        // Directory starts at sector 1: make its chain point to itself.
        var cfb = CfbWriter.Build([("WordDocument", wd), ("1Table", table)], fat => fat[1] = 1);
        var r = Fx.WithTimeout(() => Fx.Extract(cfb, "doc"));
        Assert.Equal(ExtractionStatus.Malformed, r.Status);
    }

    [Fact]
    public void BigStreamChainLoopIsMalformedQuickly()
    {
        var (wd, table) = DocStreams(0x0200, "x", "y", 6000);
        var cfb = CfbWriter.Build([("WordDocument", wd), ("1Table", table)], fat =>
        {
            // Find the regular-sector chain of WordDocument (12 sectors, the last allocated) and loop its 3rd sector back to the 1st.
            int last = Array.FindLastIndex(fat, v => v == 0xFFFFFFFE);
            int first = last - 11;
            fat[first + 2] = (uint)first;
        });
        var r = Fx.WithTimeout(() => Fx.Extract(cfb, "doc"));
        Assert.True(r.Status is ExtractionStatus.Malformed or ExtractionStatus.Ok, r.Status + " " + r.Error);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(600)]
    [InlineData(1500)]
    public void TruncatedCompoundFileIsMalformed(int length)
    {
        var doc = Doc();
        var r = Fx.WithTimeout(() => Fx.Extract(doc.AsSpan(0, Math.Min(length, doc.Length)).ToArray(), "doc"));
        Assert.Equal(ExtractionStatus.Malformed, r.Status);
    }

    [Fact]
    public void RandomCorruptionNeverEscapes()
    {
        var rnd = new Random(7);
        var docs = new[] { Doc(), Workbook(), Ppt() };
        Fx.WithTimeout(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                var b = (byte[])docs[i % 3].Clone();
                for (int k = 0; k < 20; k++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                var r = Fx.Extract(b, "doc");
                Assert.NotEqual(ExtractionStatus.Error, r.Status);
            }
            return true;
        }, 30);
    }

    [Fact]
    public void NotACompoundFile() => Assert.Equal(ExtractionStatus.Malformed, Fx.Extract("hello"u8.ToArray(), "doc").Status);
}
