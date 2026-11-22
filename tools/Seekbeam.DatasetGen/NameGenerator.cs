using System.Text;

namespace Seekbeam.Tools;

/// <summary>
/// Deterministic generator of realistic file trees (names, depth, extensions, accents, Unicode, duplicates).
/// The same seed always produces the same tree, so benchmarks are reproducible.
/// </summary>
public sealed class NameGenerator
{
    private static readonly string[] Words = (
        "rapport facture contrat cahier charges projet final version brouillon notes budget planning réunion compte rendu " +
        "présentation devis commande client fournisseur analyse étude synthèse datacenter vidéosurveillance anthropic lucens " +
        "genève lausanne zürich invoice report meeting summary design spec architecture photo image scan backup archive export " +
        "import config settings readme license changelog test data sample template modèle lettre courrier attestation " +
        "certificat diplôme cv résumé relevé bancaire impôts déclaration assurance santé voiture maison travaux plan schéma " +
        "réseau serveur sécurité audit migration déploiement été hiver printemps automne équipe ressources humaines salaire " +
        "congés formation procédure qualité norme iso sauvegarde restauration infrastructure cloud azure aws kubernetes docker " +
        "pipeline release sprint backlog roadmap stratégie marketing vente achat stock inventaire logistique transport").Split(' ');
    private static readonly string[] Unicode = ["文件", "报告", "отчёт", "документ", "αναφορά", "ファイル", "보고서", "naïve", "smörgåsbord", "crème_brûlée"];
    private static readonly (string Ext, int Weight)[] Exts =
    [
        ("pdf", 12), ("docx", 10), ("xlsx", 8), ("pptx", 4), ("txt", 8), ("md", 4), ("jpg", 14), ("png", 8), ("cs", 6), ("js", 5),
        ("ts", 4), ("json", 5), ("xml", 3), ("html", 3), ("csv", 3), ("zip", 2), ("log", 4), ("ini", 1), ("yaml", 2), ("sql", 2),
        ("py", 3), ("mp4", 2), ("mp3", 2), ("exe", 1), ("dll", 3), ("doc", 1), ("xls", 1), ("odt", 1), ("rtf", 1), ("eml", 1),
    ];
    private static readonly string[] DirWords = ["Documents", "Projets", "Archives", "Clients", "2023", "2024", "2025", "2026", "src", "Photos",
        "Factures", "Contrats", "Rapports", "Backup", "Équipe", "Exports", "Données", "Temp", "Partage", "Personnel", "Administration",
        "Comptabilité", "Juridique", "RH", "Technique", "Marketing", "Ventes", "Livrables", "Réunions", "Modèles"];

    private readonly Random _r;
    private readonly int _totalWeight;
    public NameGenerator(int seed) { _r = new Random(seed); _totalWeight = Exts.Sum(e => e.Weight); }

    public string NextExtension()
    {
        int x = _r.Next(_totalWeight);
        foreach (var (e, w) in Exts) { if (x < w) return e; x -= w; }
        return "dat";
    }

    public string NextFileName()
    {
        var sb = new StringBuilder(48);
        int style = _r.Next(10);
        if (style < 3) sb.Append(2015 + _r.Next(12)).Append((1 + _r.Next(12)).ToString("00")).Append((1 + _r.Next(28)).ToString("00")).Append('_');
        else if (style == 3) sb.Append("IMG_").Append(_r.Next(10000).ToString("0000"));
        int words = style == 3 ? 0 : 1 + _r.Next(5);
        char sep = _r.Next(3) switch { 0 => '_', 1 => ' ', _ => '-' };
        for (int k = 0; k < words; k++)
        {
            if (k > 0) sb.Append(sep);
            string w = _r.Next(60) == 0 ? Unicode[_r.Next(Unicode.Length)] : Words[_r.Next(Words.Length)];
            if (_r.Next(5) == 0) w = char.ToUpperInvariant(w[0]) + w[1..];
            sb.Append(w);
        }
        if (_r.Next(4) == 0) sb.Append(sep).Append('v').Append(1 + _r.Next(9));
        if (_r.Next(6) == 0) sb.Append(" (").Append(1 + _r.Next(3)).Append(')');
        if (_r.Next(12) != 0) sb.Append('.').Append(NextExtension());
        return sb.ToString();
    }

    public string NextDirName()
    {
        int s = _r.Next(4);
        string w = DirWords[_r.Next(DirWords.Length)];
        return s switch
        {
            0 => w,
            1 => w + " " + Words[_r.Next(Words.Length)],
            2 => Words[_r.Next(Words.Length)] + "_" + (2015 + _r.Next(12)),
            _ => char.ToUpperInvariant(Words[_r.Next(Words.Length)][0]) + Words[_r.Next(Words.Length)][1..],
        };
    }

    public int Next(int max) => _r.Next(max);
    public double NextDouble() => _r.NextDouble();

    /// <summary>
    /// Streams a tree of ≈<paramref name="totalEntries"/> entries (≈8 % directories, depth ≤ <paramref name="maxDepth"/>).
    /// Callbacks receive the parent index (-1 = root) and return the new directory index for directories.
    /// Memory: O(number of directories) ints.
    /// </summary>
    public void GenerateTree(long totalEntries, int maxDepth, Func<int, string, int, int> onDir, Action<int, string> onFile)
    {
        var depthOf = new List<byte>();
        var dirs = new List<int>(); // dir indices that can receive children
        long produced = 0;
        var used = new HashSet<long>(); // 64-bit (parent, name) hashes: 8 bytes per entry, deterministic
        bool Unique(int parent, string name) =>
            used.Add(StableHash(parent, name));
        // Top-level directories.
        int top = (int)Math.Clamp(totalEntries / 50_000, 4, 40);
        for (int i = 0; i < top; i++)
        {
            string n = NextDirName();
            if (!Unique(-1, n)) n += "_" + i;
            int idx = onDir(-1, n, 1);
            EnsureIndex(depthOf, idx, 1);
            dirs.Add(idx);
            produced++;
        }
        while (produced < totalEntries)
        {
            int parent = dirs[(int)Math.Min(dirs.Count - 1, Math.Pow(_r.NextDouble(), 0.7) * dirs.Count)];
            int depth = depthOf[parent];
            if (_r.Next(100) < 8 && depth < maxDepth)
            {
                string n = NextDirName();
                if (!Unique(parent, n)) continue;
                int idx = onDir(parent, n, depth + 1);
                EnsureIndex(depthOf, idx, (byte)(depth + 1));
                dirs.Add(idx);
            }
            else
            {
                string n = NextFileName();
                if (!Unique(parent, n)) continue;
                onFile(parent, n);
            }
            produced++;
        }
    }

    /// <summary>FNV-1a 64 over (parent, upper-cased name): stable across runs (string.GetHashCode is randomised).</summary>
    private static long StableHash(int parent, string name)
    {
        ulong h = 14695981039346656037UL ^ (uint)parent;
        foreach (char c in name.ToUpperInvariant()) { h ^= c; h *= 1099511628211UL; }
        return (long)h;
    }

    private static void EnsureIndex(List<byte> l, int idx, int v)
    {
        while (l.Count <= idx) l.Add(0);
        l[idx] = (byte)v;
    }
}
