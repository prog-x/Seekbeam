using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using Seekbeam.Core.Config;
using Seekbeam.Core.Content;
using Seekbeam.Core.SourceAccess;
using Seekbeam.Core.Storage;
using Xunit.Abstractions;

namespace Seekbeam.Core.Tests;

/// <summary>
/// Extractor host sandbox (TD-17): every isolation level starts, is verified from inside the host and extracts through a
/// brokered read-only handle; and a probe playing a compromised extractor proves what each level actually blocks.
/// </summary>
public sealed class SandboxTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekbeam-sandbox", Guid.NewGuid().ToString("N"));

    public SandboxTests(ITestOutputHelper output)
    {
        _output = output;
        // Same one-time set-up as Seekbeam.exe performs at start-up (AppContainer profile + read ACL on the binaries).
        Assert.Equal(SandboxProvisioningStatus.Ready, SandboxProvisioning.Ensure(Path.GetDirectoryName(ContentEndToEndTests.HostPath)!, _ => false));
        Assert.Equal(SandboxProvisioningStatus.Ready, SandboxProvisioning.Ensure(Path.GetDirectoryName(ProbePath)!, _ => false));
        Directory.CreateDirectory(_dir);
    }

    private static string ProbePath
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            {
                string c = Path.Combine(dir, "tests", "Seekbeam.SandboxProbe", "bin", "Debug", "net10.0-windows", "Seekbeam.SandboxProbe.exe");
                if (File.Exists(c)) return c;
            }
            throw new FileNotFoundException("Seekbeam.SandboxProbe.exe not built");
        }
    }

    private string WriteSource(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Theory]
    [InlineData(SandboxMode.Auto)]
    [InlineData(SandboxMode.AppContainer)]
    [InlineData(SandboxMode.LowIntegrity)]
    [InlineData(SandboxMode.Off)]
    public void EveryModeExtractsThroughABrokeredHandle(SandboxMode mode)
    {
        string f = WriteSource("doc.txt", "contenu isolé du document");
        using var client = new ExtractorClient(ContentEndToEndTests.HostPath, mode);
        var r = client.Extract(new ExtractRequest { Path = f }, TimeSpan.FromSeconds(30), default);
        _output.WriteLine($"{mode}: level {client.Level}, status {r.Status} {r.Error}; last sandbox failure: {ExtractorClient.LastFailure}");
        Assert.Equal(ExtractStatus.Ok, r.Status);
        Assert.Contains("isolé", r.Text);
        var expected = mode switch
        {
            SandboxMode.Off => SandboxLevel.None,
            SandboxMode.LowIntegrity => SandboxLevel.LowIntegrity,
            _ => SandboxLevel.AppContainer,
        };
        Assert.Equal(expected, client.Level);
    }

    [Fact]
    public void SandboxedHostExtractsNestedArchivesThroughTheHandle()
    {
        string zip = Path.Combine(_dir, "a.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("inner/note.txt").Open())) w.Write("texte dans une archive");
        using var client = new ExtractorClient(ContentEndToEndTests.HostPath);
        var r = client.Extract(new ExtractRequest { Path = zip }, TimeSpan.FromSeconds(30), default);
        _output.WriteLine($"zip: {r.Status} {r.Error}");
        Assert.Equal(ExtractStatus.Ok, r.Status);
        Assert.Contains("archive", r.Text);
        Assert.Equal(SandboxLevel.AppContainer, client.Level);
    }

    [Fact]
    public void MissingSourceIsReportedWithoutStartingAnExtraction()
    {
        using var client = new ExtractorClient(ContentEndToEndTests.HostPath);
        var r = client.Extract(new ExtractRequest { Path = Path.Combine(_dir, "absent.txt") }, TimeSpan.FromSeconds(30), default);
        Assert.Equal(ExtractStatus.Error, r.Status);
        Assert.Equal("not found", r.Error);
    }

    [Fact]
    public void BrokeredHandleCannotWriteAndWriteHandlesAreRefused()
    {
        string f = WriteSource("ro.txt", "original");
        long value;
        using (var src = SourceFileAccess.OpenRead(f))
            value = SourceFileAccess.DuplicateReadHandleInto(src, Process.GetCurrentProcess().SafeHandle);
        using (var dup = new SafeFileHandle((IntPtr)value, ownsHandle: true))
        {
            // The kernel itself refuses writes through the duplicated handle (FILE_GENERIC_READ only).
            Assert.ThrowsAny<UnauthorizedAccessException>(() => RandomAccess.Write(dup, "x"u8.ToArray(), 0));
            Assert.ThrowsAny<UnauthorizedAccessException>(() => RandomAccess.SetLength(dup, 0));
        }
        Assert.Equal("original", File.ReadAllText(f));

        // A handle carrying write rights is refused by the receiving side (and closed).
        var rw = File.OpenHandle(f, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        IntPtr raw = rw.DangerousGetHandle();
        rw.SetHandleAsInvalid(); // ownership moves to FromReceivedHandle
        Assert.Throws<SourceAccessException>(() => SourceFileAccess.FromReceivedHandle(raw.ToInt64()));

        // The legitimate read-only handle is accepted and readable.
        using (var src = SourceFileAccess.OpenRead(f))
            value = SourceFileAccess.DuplicateReadHandleInto(src, Process.GetCurrentProcess().SafeHandle);
        using (var s = SourceFileAccess.FromReceivedHandle(value))
            Assert.Equal("original", new StreamReader(s).ReadToEnd());
    }

    /// <summary>
    /// What a compromised extractor can still do, per level. "OK" = allowed, "DENIED" = blocked by Windows.
    /// Low integrity can still read user files and use the network: that is why AppContainer is preferred (TD-17).
    /// </summary>
    [Theory]
    //          level                      read      write     connect   spawn     registry
    [InlineData(SandboxLevel.AppContainer, "DENIED", "DENIED", "DENIED", "DENIED", "DENIED")]
    [InlineData(SandboxLevel.LowIntegrity, "OK",     "DENIED", "OK",     "DENIED", "DENIED")]
    [InlineData(SandboxLevel.None,         "OK",     "OK",     "OK",     "DENIED", "OK")]
    public void SandboxConfinesACompromisedExtractor(SandboxLevel level, string read, string write, string connect, string spawn, string registry)
    {
        string secret = WriteSource("secret.txt", "user secret");
        string writableDir = Path.Combine(_dir, "target");
        Directory.CreateDirectory(writableDir);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IntPtr job = ExtractorClient.CreateConfiguredJob(256, 50);
        try
        {
            using var p = SandboxLauncher.Start(ProbePath, level, job);
            using var w = new StreamWriter(p.StdIn) { AutoFlush = true };
            using var r = new StreamReader(p.StdOut);
            string Ask(string cmd)
            {
                w.WriteLine(cmd);
                var t = r.ReadLineAsync();
                Assert.True(t.Wait(TimeSpan.FromSeconds(20)), "probe did not answer " + cmd);
                string ans = t.Result ?? "EOF";
                _output.WriteLine($"{level} {cmd.Split(' ')[0]}: {ans}");
                return ans.Split(' ')[0];
            }
            Assert.Equal(read, Ask("read " + secret));
            Assert.Equal(write, Ask("write " + writableDir));
            Assert.Equal(connect, Ask("connect " + port));
            Assert.Equal(spawn, Ask("spawn"));
            Assert.Equal(registry, Ask("reg"));
        }
        finally
        {
            listener.Stop();
            ExtractorClient.CloseJob(job);
            CleanProbeRegistryKeys();
        }
        if (write == "DENIED") Assert.Empty(Directory.GetFiles(writableDir));
    }

    private static void CleanProbeRegistryKeys()
    {
        using var sw = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", true);
        if (sw == null) return;
        foreach (var k in sw.GetSubKeyNames().Where(n => n.StartsWith("Seekbeam-sandbox-probe-", StringComparison.Ordinal)))
            sw.DeleteSubKeyTree(k, false);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
