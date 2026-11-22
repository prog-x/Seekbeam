namespace Seekbeam.Extraction.Formats;

/// <summary>Plain text, source code, configuration files and logs.</summary>
internal sealed class PlainTextExtractor : IContentExtractor
{
    public string Id => "text";
    public int Version => 1;

    public IReadOnlyCollection<string> Extensions { get; } =
    [
        "txt", "md", "markdown", "csv", "tsv", "log", "ini", "cfg", "conf", "toml", "yaml", "yml", "json", "jsonc", "sql",
        "css", "scss", "less", "js", "mjs", "cjs", "ts", "tsx", "jsx", "py", "rb", "php", "java", "kt", "kts", "scala",
        "c", "h", "cpp", "hpp", "cc", "cs", "vb", "fs", "go", "rs", "swift", "m", "mm", "r", "pl", "ps1", "psm1", "bat",
        "cmd", "sh", "bash", "zsh", "lua", "dart", "groovy", "gradle", "properties", "env", "editorconfig", "gitignore",
        "dockerfile", "makefile", "cmake", "tex", "bib", "srt", "vtt", "nfo", "rst", "adoc", "asciidoc", "org", "diff",
        "patch", "reg", "inf", "vbs", "ahk", "tf", "hcl", "proto", "graphql", "xaml", "csproj", "vbproj", "sln", "props",
        "targets",
    ];

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct) =>
        TextDecoding.DecodeStream(source, honourDeclaration: false, chars => sink.Append(chars), () => sink.IsFull, ct);
}
