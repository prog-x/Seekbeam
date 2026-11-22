using System.Buffers.Binary;
using System.Text;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Apps;

public sealed record ShellLinkInfo(string? Target, string? Arguments, string? WorkingDirectory, string? IconLocation, int IconIndex, string? Description);

/// <summary>
/// Read-only parser of Windows shortcuts (MS-SHLLINK). Deliberately NOT IShellLink: IShellLink::Resolve may rewrite the
/// .lnk file, and IPersistFile::Load opens it with flags we do not control (TD-12). Bytes come from SourceFileAccess.
/// </summary>
public static class LnkParser
{
    private const int HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8, HasWorkingDir = 0x10,
        HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80, HasExpString = 0x200;

    public static ShellLinkInfo? TryParseFile(string path)
    {
        try { return TryParse(SourceFileAccess.ReadAllBytesBounded(path, 1 << 20)); }
        catch (SourceAccessException) { return null; }
    }

    public static ShellLinkInfo? TryParse(ReadOnlySpan<byte> b)
    {
        try { return Parse(b); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    private static ShellLinkInfo? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 0x4C || BinaryPrimitives.ReadInt32LittleEndian(b) != 0x4C) return null;
        // LinkCLSID 00021401-0000-0000-C000-000000000046
        if (b[4] != 0x01 || b[5] != 0x14 || b[6] != 0x02 || b[7] != 0x00) return null;
        int flags = BinaryPrimitives.ReadInt32LittleEndian(b[0x14..]);
        int iconIndex = BinaryPrimitives.ReadInt32LittleEndian(b[0x38..]);
        int pos = 0x4C;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            int idListSize = BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
            pos += 2 + idListSize;
        }
        string? target = null;
        if ((flags & HasLinkInfo) != 0)
        {
            int liStart = pos;
            int liSize = BinaryPrimitives.ReadInt32LittleEndian(b[pos..]);
            if (liSize < 0x1C || liStart + liSize > b.Length) return null;
            var li = b.Slice(liStart, liSize);
            int liHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(li[4..]);
            int liFlags = BinaryPrimitives.ReadInt32LittleEndian(li[8..]);
            int localBaseOff = BinaryPrimitives.ReadInt32LittleEndian(li[16..]);
            int netOff = BinaryPrimitives.ReadInt32LittleEndian(li[20..]);
            int suffixOff = BinaryPrimitives.ReadInt32LittleEndian(li[24..]);
            string? localBase = null, suffix = null;
            if (liHeaderSize >= 0x24)
            {
                int ulb = BinaryPrimitives.ReadInt32LittleEndian(li[28..]);
                int usf = BinaryPrimitives.ReadInt32LittleEndian(li[32..]);
                if ((liFlags & 1) != 0 && ulb > 0) localBase = ReadUnicodeZ(li, ulb);
                if (usf > 0) suffix = ReadUnicodeZ(li, usf);
            }
            if ((liFlags & 1) != 0 && localBase == null && localBaseOff > 0) localBase = ReadAnsiZ(li, localBaseOff);
            suffix ??= suffixOff > 0 ? ReadAnsiZ(li, suffixOff) : null;
            if (localBase != null) target = Combine(localBase, suffix);
            else if ((liFlags & 2) != 0 && netOff > 0 && netOff + 20 <= li.Length)
            {
                var net = li[netOff..];
                int netNameOff = BinaryPrimitives.ReadInt32LittleEndian(net[8..]);
                string? netName = null;
                if (netNameOff > 0x14 && net.Length >= 28)
                {
                    int u = BinaryPrimitives.ReadInt32LittleEndian(net[20..]);
                    if (u > 0) netName = ReadUnicodeZ(net, u);
                }
                netName ??= ReadAnsiZ(net, netNameOff);
                if (netName != null) target = Combine(netName, suffix);
            }
            pos = liStart + liSize;
        }
        bool unicode = (flags & IsUnicode) != 0;
        static string? Next(ReadOnlySpan<byte> b, int flags, int flag, ref int pos, bool unicode)
        {
            if ((flags & flag) == 0) return null;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
            pos += 2;
            int bytes = unicode ? count * 2 : count;
            var s = b.Slice(pos, bytes);
            pos += bytes;
            return unicode ? Encoding.Unicode.GetString(s) : Encoding.Latin1.GetString(s);
        }
        string? name = Next(b, flags, HasName, ref pos, unicode), rel = Next(b, flags, HasRelativePath, ref pos, unicode),
            workdir = Next(b, flags, HasWorkingDir, ref pos, unicode), args = Next(b, flags, HasArguments, ref pos, unicode),
            icon = Next(b, flags, HasIconLocation, ref pos, unicode);
        // ExtraData: EnvironmentVariableDataBlock (target with %VARS%) when the shortcut uses environment strings.
        while ((flags & HasExpString) != 0 && pos + 8 <= b.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(b[pos..]);
            if (size < 4 || pos + size > b.Length) break;
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(b[(pos + 4)..]);
            if (sig == 0xA0000001 && size >= 0x314)
            {
                string env = ReadUnicodeZ(b.Slice(pos + 8 + 260, 520), 0) ?? ReadAnsiZ(b.Slice(pos + 8, 260), 0) ?? "";
                if (env.Length > 0) target = Environment.ExpandEnvironmentVariables(env);
                break;
            }
            pos += size;
        }
        if (target == null && rel != null && workdir != null) target = Path.GetFullPath(Path.Combine(workdir, rel));
        if (icon != null) icon = Environment.ExpandEnvironmentVariables(icon);
        if (workdir != null) workdir = Environment.ExpandEnvironmentVariables(workdir);
        return new ShellLinkInfo(target, args, workdir, icon, iconIndex, name);
    }

    private static string Combine(string a, string? b) => string.IsNullOrEmpty(b) ? a : a.EndsWith('\\') ? a + b : a + "\\" + b;

    private static string? ReadAnsiZ(ReadOnlySpan<byte> s, int off)
    {
        if (off < 0 || off >= s.Length) return null;
        int end = s[off..].IndexOf((byte)0);
        if (end < 0) end = s.Length - off;
        return end == 0 ? null : Encoding.Latin1.GetString(s.Slice(off, end));
    }

    private static string? ReadUnicodeZ(ReadOnlySpan<byte> s, int off)
    {
        if (off < 0 || off >= s.Length) return null;
        int i = off;
        while (i + 1 < s.Length && (s[i] != 0 || s[i + 1] != 0)) i += 2;
        return i == off ? null : Encoding.Unicode.GetString(s[off..i]);
    }
}
