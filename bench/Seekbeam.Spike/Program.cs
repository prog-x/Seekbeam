// Spike: compare candidate Name Index engines on N synthetic file names.
// A) SQLite FTS5 trigram   B) contiguous UTF-8 byte scan (vectorised IndexOf)   C) SQLite LIKE scan
// Usage: Seekbeam.Spike [count=2000000]
using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;

int n = args.Length > 0 ? int.Parse(args[0]) : 2_000_000;
var names = Gen.Names(n, 42);
long totalChars = names.Sum(s => (long)s.Length);
Console.WriteLine($"Generated {n:N0} names, avg {totalChars / (double)n:F1} chars");
string[] queries = { "ant 202611 cahier", "rapport", "doc", "facture 2024", "pdf", "a", "zzqx", "surveillance data", "proj 2023 final" };

// ---------- B: byte scan ----------
var sw = Stopwatch.StartNew();
var buf = new MemoryStream();
var offsets = new int[n + 1];
for (int i = 0; i < n; i++)
{
    offsets[i] = (int)buf.Position;
    var b = Encoding.UTF8.GetBytes(names[i].ToLowerInvariant());
    buf.Write(b); buf.WriteByte((byte)'\n');
}
offsets[n] = (int)buf.Position;
byte[] blob = buf.ToArray();
Console.WriteLine($"[Scan] build {sw.ElapsedMilliseconds} ms, blob {blob.Length / 1048576.0:F1} MB");
foreach (var q in queries)
{
    var toks = q.Split(' ').Select(t => Encoding.UTF8.GetBytes(t)).OrderByDescending(t => t.Length).ToArray();
    int hits = 0;
    Scan.Run(blob, offsets, toks, 1, ref hits); // warm
    var t = Stopwatch.StartNew();
    for (int r = 0; r < 5; r++) { hits = 0; Scan.Run(blob, offsets, toks, 1, ref hits); }
    double single = t.Elapsed.TotalMilliseconds / 5;
    t.Restart();
    for (int r = 0; r < 5; r++) { hits = 0; Scan.Run(blob, offsets, toks, 8, ref hits); }
    double par = t.Elapsed.TotalMilliseconds / 5;
    Console.WriteLine($"[Scan] '{q}': {hits,9:N0} hits  1T {single,7:F2} ms  8T {par,7:F2} ms");
}

// ---------- A/C: SQLite ----------
string dbPath = Path.Combine(Path.GetTempPath(), "seekbeam_spike.db");
foreach (var ext in new[] { "", "-wal", "-shm" }) File.Delete(dbPath + ext);
using var con = new SqliteConnection($"Data Source={dbPath}");
con.Open();
Exec(con, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
Exec(con, "CREATE TABLE names(id INTEGER PRIMARY KEY, name TEXT);");
Exec(con, "CREATE VIRTUAL TABLE fts USING fts5(name, content='names', content_rowid='id', tokenize='trigram remove_diacritics 1');");
sw.Restart();
using (var tx = con.BeginTransaction())
{
    var cmd = con.CreateCommand();
    cmd.CommandText = "INSERT INTO names(id,name) VALUES($id,$n)";
    var pid = cmd.Parameters.Add("$id", SqliteType.Integer); var pn = cmd.Parameters.Add("$n", SqliteType.Text);
    for (int i = 0; i < n; i++) { pid.Value = i + 1; pn.Value = names[i]; cmd.ExecuteNonQuery(); }
    tx.Commit();
}
long tIns = sw.ElapsedMilliseconds; sw.Restart();
Exec(con, "INSERT INTO fts(fts) VALUES('rebuild');");
long tFts = sw.ElapsedMilliseconds;
Exec(con, "PRAGMA wal_checkpoint(TRUNCATE);");
Console.WriteLine($"[SQLite] insert {tIns} ms, fts trigram build {tFts} ms, db {new FileInfo(dbPath).Length / 1048576.0:F1} MB");
foreach (var q in queries)
{
    var toks = q.Split(' ');
    var tri = toks.Where(t => t.Length >= 3).Select(t => "\"" + t + "\"").ToArray();
    var shortToks = toks.Where(t => t.Length < 3).ToArray();
    string sql = tri.Length > 0
        ? "SELECT count(*) FROM fts WHERE fts MATCH $m" + string.Concat(shortToks.Select((s, i) => $" AND name LIKE '%{s}%'"))
        : "SELECT count(*) FROM names WHERE " + string.Join(" AND ", shortToks.Select(s => $"name LIKE '%{s}%'"));
    var c = con.CreateCommand(); c.CommandText = sql; c.Parameters.AddWithValue("$m", string.Join(" AND ", tri));
    c.ExecuteScalar();
    var t = Stopwatch.StartNew(); long cnt = 0;
    for (int r = 0; r < 3; r++) cnt = (long)c.ExecuteScalar()!;
    double fts = t.Elapsed.TotalMilliseconds / 3;
    var c2 = con.CreateCommand();
    c2.CommandText = "SELECT count(*) FROM names WHERE " + string.Join(" AND ", toks.Select(s => $"name LIKE '%{s}%'"));
    t.Restart(); long cnt2 = (long)c2.ExecuteScalar()!; double like = t.Elapsed.TotalMilliseconds;
    Console.WriteLine($"[SQLite] '{q}': FTS {cnt,9:N0} hits {fts,8:F2} ms | LIKE {cnt2,9:N0} hits {like,8:F2} ms");
}
Console.WriteLine($"Process private MB: {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576}");

static void Exec(SqliteConnection c, string sql) { var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

static class Scan
{
    public static void Run(byte[] blob, int[] offsets, byte[][] toks, int threads, ref int hits)
    {
        int chunks = threads;
        int total = 0;
        int n = offsets.Length - 1;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = threads }, c =>
        {
            int lo = (int)((long)n * c / chunks), hi = (int)((long)n * (c + 1) / chunks);
            int local = 0;
            int pos = offsets[lo], end = offsets[hi];
            var first = toks[0];
            while (pos < end)
            {
                int idx = blob.AsSpan(pos, end - pos).IndexOf(first);
                if (idx < 0) break;
                int at = pos + idx;
                int rec = Array.BinarySearch(offsets, lo, hi - lo + 1, at);
                if (rec < 0) rec = ~rec - 1;
                int rs = offsets[rec], re = offsets[rec + 1] - 1;
                var name = blob.AsSpan(rs, re - rs);
                bool ok = true;
                for (int k = 1; k < toks.Length && ok; k++) ok = name.IndexOf(toks[k]) >= 0;
                if (ok) local++;
                pos = re + 1;
            }
            Interlocked.Add(ref total, local);
        });
        hits = total;
    }
}

static class Gen
{
    static readonly string[] Words = ("rapport facture contrat cahier charges projet final version draft notes budget planning reunion " +
        "compte rendu presentation devis commande client fournisseur analyse etude synthese datacenter videosurveillance " +
        "anthropic lucens geneve lausanne zurich invoice report meeting summary design spec architecture photo image scan " +
        "backup archive export import config settings readme license changelog test data sample template modele lettre " +
        "courrier attestation certificat diplome cv resume releve bancaire impots declaration assurance sante voiture maison " +
        "travaux plan schema reseau serveur securite audit migration deploiement").Split(' ');
    static readonly string[] Exts = "pdf docx xlsx pptx txt md jpg png cs js ts json xml html csv zip log ini yaml sql py".Split(' ');
    public static string[] Names(int n, int seed)
    {
        var r = new Random(seed);
        var res = new string[n];
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Clear();
            if (r.Next(3) == 0) sb.Append(2015 + r.Next(12)).Append((1 + r.Next(12)).ToString("00")).Append((1 + r.Next(28)).ToString("00")).Append('_');
            int w = 1 + r.Next(6);
            for (int k = 0; k < w; k++) { if (k > 0) sb.Append(r.Next(2) == 0 ? '_' : ' '); sb.Append(Words[r.Next(Words.Length)]); }
            if (r.Next(4) == 0) sb.Append('_').Append(r.Next(1000));
            if (r.Next(10) != 0) sb.Append('.').Append(Exts[r.Next(Exts.Length)]);
            res[i] = sb.ToString();
        }
        return res;
    }
}
