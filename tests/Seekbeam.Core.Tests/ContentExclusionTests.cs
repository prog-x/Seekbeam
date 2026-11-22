using Seekbeam.Core.Config;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Tests;

/// <summary>Downloads excluded from content extraction by default (SECURITY.md) and the perimeter test guarding sandbox set-up.</summary>
public sealed class ContentExclusionTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "seekbeam-excl", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultConfigurationExcludesDownloadsContent()
    {
        var c = SeekbeamConfig.CreateDefault();
        Assert.Contains(c.Content.ExcludedFolders, KnownFolders.IsDownloads);
        Assert.Equal(SeekbeamConfig.CurrentSchemaVersion, c.SchemaVersion);
    }

    [Fact]
    public void OlderConfigurationIsMigratedToExcludeDownloadsAndRewritten()
    {
        var storage = new SeekbeamStorage(Path.Combine(_base, "data"));
        storage.WriteAllTextAtomic("config.json", """{ "SchemaVersion": 1, "Roots": [], "Content": { "Enabled": true } }""");
        var store = new ConfigStore(storage);
        Assert.Contains(store.Current.Content.ExcludedFolders, KnownFolders.IsDownloads);
        string saved = storage.ReadAllText("config.json")!;
        Assert.Contains("\"SchemaVersion\": 2", saved);
        Assert.Contains("ExcludedFolders", saved);

        // A user who re-allowed Downloads after the migration keeps that choice.
        store.Update(c => c.Content.ExcludedFolders = []);
        Assert.Empty(new ConfigStore(storage).Current.Content.ExcludedFolders);
    }

    [Fact]
    public void ExcludedFolderMakesFilesIneligibleButNotTheirSiblings()
    {
        var cfg = new ContentConfig { ExcludedFolders = [@"C:\Users\u\Downloads"] };
        var elig = new ContentEligibility(cfg, ["txt", "pdf"]);
        var root = new RootScope { RootId = 1, Path = @"C:\Users\u", Config = new RootConfig { Path = @"C:\Users\u" }, Excludes = new ExclusionMatcher([]) };
        Assert.False(elig.IsEligible(root, @"C:\Users\u\Downloads\invoice.pdf", 10, FileAttributes.Normal));
        Assert.False(elig.IsEligible(root, @"C:\Users\u\downloads\sub\a.txt", 10, FileAttributes.Normal)); // case-insensitive, recursive
        Assert.True(elig.IsEligible(root, @"C:\Users\u\Downloads2\a.txt", 10, FileAttributes.Normal));    // prefix of a name is not a match
        Assert.True(elig.IsEligible(root, @"C:\Users\u\Documents\a.txt", 10, FileAttributes.Normal));
    }

    [Fact]
    public void ValidationNormalizesAndDeduplicatesExcludedFolders()
    {
        var c = new SeekbeamConfig();
        c.Content.ExcludedFolders = [@"C:\Data\Dl\", @"c:\data\dl", "  ", @"C:\Other"];
        c.Validate();
        Assert.Equal([@"C:\Data\Dl", @"C:\Other"], c.Content.ExcludedFolders);
    }

    [Fact]
    public void TouchesScopeProtectsRootsTheirContentAndTheirAncestors()
    {
        var cfg = new SeekbeamConfig { Roots = [new RootConfig { Path = @"C:\Users\u", Excludes = ["AppData"] }] };
        var scope = ScopeRules.FromConfig(cfg, @"C:\Users\u\AppData\Local\Seekbeam");
        Assert.True(scope.TouchesScope(@"C:\Users\u\Documents"));          // inside the perimeter
        Assert.True(scope.TouchesScope(@"C:\Users"));                      // contains a root: an inheritable ACE would reach it
        Assert.False(scope.TouchesScope(@"C:\Users\u\AppData\Local\Programs\Seekbeam")); // excluded subtree
        Assert.False(scope.TouchesScope(@"C:\Program Files\Seekbeam"));
    }

    [Fact]
    public void PortableDataDirectoryIsNeverReadableByAppContainers()
    {
        // Portable layout: <program>\Seekbeam.ExtractorHost.exe and <program>\Data\db\catalog.db.
        string program = Path.Combine(_base, "portable"), data = Path.Combine(program, "Data");
        Directory.CreateDirectory(Path.Combine(data, "db"));
        File.WriteAllText(Path.Combine(program, "Seekbeam.ExtractorHost.exe"), "binary");
        File.WriteAllText(Path.Combine(data, "db", "catalog.db"), "index with extracted text");

        Assert.Equal(SandboxProvisioningStatus.Ready, SandboxProvisioning.Ensure(program, _ => false, data));
        Assert.True(SandboxProvisioning.HasAppContainerRead(program));             // the sandbox can load its code…
        Assert.False(SandboxProvisioning.IsReadableByAppContainers(data));         // …but never read the index
        File.WriteAllText(Path.Combine(data, "db", "content.db"), "created later");
        Assert.False(SandboxProvisioning.IsReadableByAppContainers(data));         // files created afterwards included

        // Idempotent.
        Assert.Equal(SandboxProvisioningStatus.Ready, SandboxProvisioning.Ensure(program, _ => false, data));
        Assert.False(SandboxProvisioning.IsReadableByAppContainers(data));

        // Repairs a data directory exposed by an earlier version (grant made without knowing the data directory).
        string program2 = Path.Combine(_base, "portable2"), data2 = Path.Combine(program2, "Data");
        Directory.CreateDirectory(data2);
        File.WriteAllText(Path.Combine(data2, "catalog.db"), "index");
        Assert.Equal(SandboxProvisioningStatus.Ready, SandboxProvisioning.Ensure(program2, _ => false));
        Assert.True(SandboxProvisioning.IsReadableByAppContainers(data2));        // the exposure being fixed
        Assert.Equal(SandboxProvisioningStatus.Ready, SandboxProvisioning.Ensure(program2, _ => false, data2));
        Assert.False(SandboxProvisioning.IsReadableByAppContainers(data2));
        Assert.True(SandboxProvisioning.HasAppContainerRead(program2));

        // A program directory inside the data directory is refused outright.
        Assert.Equal(SandboxProvisioningStatus.Failed, SandboxProvisioning.Ensure(Path.Combine(data, "bin"), _ => false, data));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }
}
