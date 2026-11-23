using Seekbeam.Core.Apps;
using Seekbeam.Core.Config;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Tests;

/// <summary>Win+R commands (PATH, App Paths, .msc/.cpl) and user aliases in the launcher.</summary>
public sealed class RunCommandsTests : IDisposable
{
    private readonly TestEnv _env = new();
    private readonly string _bin;

    public RunCommandsTests()
    {
        _bin = Path.Combine(_env.Root, "bin");
        Directory.CreateDirectory(_bin);
        long root = _env.AddRoot(@"C:\Users\Test\Documents");
        _env.Add(root, "mstsc_notes.txt");
        _env.Add(root, "msdt.log");
        _env.Add(root, "hosts_backup.txt");
        _env.Add(root, "reg_export.reg");
    }

    public void Dispose() => _env.Dispose();

    private static AppEntry Cmd(string name, string path) => AppEntry.Create(name, path, AppSource.RunCommand, path);

    private void Catalog(params AppEntry[] extra) => _env.Apps.Replace(
    [
        Cmd("mstsc", @"C:\Windows\System32\mstsc.exe"),
        Cmd("msdt", @"C:\Windows\System32\msdt.exe"),
        Cmd("mshta", @"C:\Windows\System32\mshta.exe"),
        Cmd("regedit", @"C:\Windows\regedit.exe"),
        Cmd("services.msc", @"C:\Windows\System32\services.msc"),
        Cmd("appwiz.cpl", @"C:\Windows\System32\appwiz.cpl"),
        .. extra,
    ]);

    [Fact]
    public void PathListIsExpandedCleanedAndDeduplicated()
    {
        string sysroot = Environment.GetEnvironmentVariable("SystemRoot")!;
        var dirs = RunCommands.ParsePathList(@"%SystemRoot%\System32;""C:\Quoted Dir\"";;relative\dir;%NOPE_UNDEFINED%\x;C:\WINDOWS\system32\;C:\Tools");
        Assert.Equal([Path.Combine(sysroot, "System32"), @"C:\Quoted Dir", @"C:\Tools"], dirs);
    }

    [Fact]
    public void FolderScanFollowsCmdResolutionAndKeepsMscCplExtensions()
    {
        foreach (var f in new[] { "tool.cmd", "tool.exe", "build.bat", "services.msc", "appwiz.cpl", "readme.txt", "lib.dll" })
            File.WriteAllText(Path.Combine(_bin, f), "");
        Directory.CreateDirectory(Path.Combine(_bin, "sub.exe")); // a folder is never a command
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build" }; // already resolved in an earlier PATH folder
        RunCommands.AddFolder(_bin, list, seen);
        var byName = list.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["appwiz.cpl", "services.msc", "tool"], byName.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.EndsWith("tool.exe", byName["tool"].LaunchPath); // .exe before .cmd, as cmd.exe resolves it
        Assert.All(list, a => Assert.Equal(AppSource.RunCommand, a.Source));
    }

    [Fact]
    public void RealSystemCommandsAreFound()
    {
        var all = RunCommands.Scan();
        var names = all.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("mstsc", names);
        Assert.Contains("notepad", names);
        Assert.Contains("services.msc", names);
        Assert.Contains("appwiz.cpl", names);
        Assert.Equal(names.Count, all.Count); // one entry per name
        Assert.All(all, a => Assert.True(Path.IsPathFullyQualified(a.LaunchPath), a.LaunchPath));
    }

    [Fact]
    public void ExactCommandRanksFirstAndAboveFiles()
    {
        Catalog();
        var r = _env.Search("mstsc").Results;
        Assert.Equal("mstsc", r[0].Name);
        Assert.Equal(ResultKind.App, r[0].Kind);
        Assert.Contains(r, x => x.Name == "mstsc_notes.txt");
        Assert.Equal("services.msc", _env.Names("services").First());
        Catalog(Cmd("services", @"C:\Windows\System32\services.exe"));
        Assert.Equal("services.msc", _env.Names("services").First()); // the console before the same-named process
        Catalog();
        Assert.Equal("services.msc", _env.Names("services.msc").First());
        Assert.Equal("appwiz.cpl", _env.Names("appwiz").First());
    }

    [Fact]
    public void CommandsNeverMatchByShortPrefixSubstringOrTypo()
    {
        Catalog();
        Assert.DoesNotContain("msdt", _env.Names("ms"));         // < 3 letters: no flood of ms*.exe
        Assert.Contains("msdt", _env.Names("msd"));              // prefix of 3 letters
        Assert.DoesNotContain("regedit", _env.Names("edit"));    // no substring
        Assert.DoesNotContain("mstsc", _env.Names("mstcs"));     // no typo tolerance
        Assert.DoesNotContain("mstsc", _env.Names("mstsc notes")); // single word only
        // A prefix does not get the application bonus: it does not outrank an exact command.
        Catalog(Cmd("reg", @"C:\Windows\System32\reg.exe"));
        Assert.Equal("reg", _env.Names("reg").First());
    }

    [Fact]
    public void CommandFollowedByArgumentsIsOfferedFirst()
    {
        Catalog();
        var top = _env.Search("mstsc /v:server01").Results[0];
        Assert.Equal("mstsc /v:server01", top.Name);
        Assert.Equal("/v:server01", top.Arguments);
        Assert.Equal(@"C:\Windows\System32\mstsc.exe", top.App!.LaunchPath);
        Assert.Equal(new Highlight(0, 5), Assert.Single(top.Highlights));
        Assert.Equal("/v:x", _env.Search("mstsc.exe /v:x").Results[0].Arguments); // explicit extension
        Assert.DoesNotContain(_env.Search("mst /v:x").Results, x => x.Arguments != null); // exact name only
        Assert.DoesNotContain(_env.Search("notacommand foo").Results, x => x.Arguments != null);
    }

    [Fact]
    public void AliasIsFoundByKeywordAndWinsOverCommandOfSameName()
    {
        var hosts = AppEntry.FromAlias(AliasConfig.Hosts())!;
        var mine = AppEntry.FromAlias(new AliasConfig { Keyword = "mstsc", Target = "mstsc.exe", Arguments = "/v:home" })!;
        Catalog(hosts, mine);
        string sysroot = Environment.GetEnvironmentVariable("SystemRoot")!;
        Assert.Equal(Path.Combine(sysroot, @"System32\notepad.exe"), hosts.LaunchPath); // variables expanded
        Assert.Equal(Path.Combine(sysroot, @"System32\drivers\etc\hosts"), hosts.LaunchArguments);
        Assert.True(hosts.RunAsAdmin);

        var r = _env.Search("hosts").Results;
        Assert.Same(hosts, r[0].App);
        Assert.DoesNotContain(_env.Search("notepad").Results, x => x.App == hosts); // never found by its target
        Assert.Same(mine, _env.Search("mstsc").Results[0].App);
        Assert.Equal(AppSource.RunCommand, _env.Search("mstsc").Results[1].App!.Source); // the plain command stays reachable
        Assert.True(_env.Apps.TryGetCommand("mstsc", out var typed) && typed == mine);  // "mstsc args" uses the alias

        var withArgs = _env.Search("hosts extra").Results[0];
        Assert.Same(hosts, withArgs.App);
        Assert.Equal(hosts.LaunchArguments + " extra", AppEntry.JoinArguments(hosts.LaunchArguments, withArgs.Arguments));
        Assert.Equal("alias:hosts", hosts.Key); // usage learning is not shared with Notepad
    }

    [Fact]
    public void AliasesAreValidatedAndHostsIsAddedOnceByMigration()
    {
        Assert.Contains(SeekbeamConfig.CreateDefault().Apps.Aliases, a => a.Keyword == "hosts");

        var c = new SeekbeamConfig();
        c.Apps.Aliases =
        [
            new() { Keyword = " logs ", Target = @"C:\Logs" },
            new() { Keyword = "LOGS", Target = @"C:\Other" },
            new() { Keyword = "two words", Target = "x.exe" },
            new() { Keyword = "", Target = "x.exe" },
            new() { Keyword = "empty", Target = " " },
        ];
        var problems = c.Validate();
        Assert.Equal("logs", Assert.Single(c.Apps.Aliases).Keyword);
        Assert.Equal(4, problems.Count);

        string dir = Path.Combine(_env.Root, "cfg");
        var storage = new SeekbeamStorage(dir);
        storage.WriteAllTextAtomic("config.json", """{ "SchemaVersion": 2, "Roots": [] }""");
        var store = new ConfigStore(storage);
        Assert.Contains(store.Current.Apps.Aliases, a => a.Keyword == "hosts");
        Assert.True(store.Current.Apps.RunCommands);
        // Deleted by the user: it does not come back.
        store.Update(x => x.Apps.Aliases = []);
        Assert.Empty(new ConfigStore(storage).Current.Apps.Aliases);
    }

    [Fact]
    public void ScannerPublishesAliasesAndHonoursTheRunCommandsOption()
    {
        var cat = new AppCatalog();
        using var s = new AppScanner(cat);
        s.Configure(new AppsConfig { StartMenu = false, Desktop = false, PackagedApps = false, RunCommands = false, Aliases = [AliasConfig.Hosts()] });
        s.Refresh();
        Assert.Equal("hosts", Assert.Single(cat.All).Name);
        Assert.False(cat.TryGetCommand("mstsc", out _));

        s.Configure(new AppsConfig { StartMenu = false, Desktop = false, PackagedApps = false, RunCommands = true });
        s.Refresh();
        Assert.True(cat.TryGetCommand("MSTSC.exe", out var m));
        Assert.Equal(AppSource.RunCommand, m.Source);
        Assert.DoesNotContain(cat.All, a => a.Source == AppSource.Alias);
    }
}
