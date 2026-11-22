using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Win32;

// Plays the part of an extractor host taken over by a malicious document: it tries what an attacker would try, and
// reports each outcome. Protocol: one command per line on stdin, one result line on stdout ("OK" or "DENIED <reason>").
//   read <file>      open and read a user file by path          write <dir>    create a file in a user directory
//   connect <port>   TCP connection to 127.0.0.1:<port>          spawn          start a child process (cmd.exe)
//   reg              create a key under HKCU\Software            env            print the integrity of its own token (diagnostic)
var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
var stdin = new StreamReader(Console.OpenStandardInput());
string? line;
while ((line = stdin.ReadLine()) != null)
{
    string[] parts = line.Split(' ', 2);
    string arg = parts.Length > 1 ? parts[1] : "";
    string result;
    try
    {
        switch (parts[0])
        {
            case "read":
                _ = File.ReadAllBytes(arg);
                break;
            case "write":
                string p = Path.Combine(arg, "probe-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(p, "written from the sandbox");
                break;
            case "connect":
                using (var c = new TcpClient())
                {
                    if (!c.ConnectAsync("127.0.0.1", int.Parse(arg)).Wait(1500)) throw new TimeoutException("connect timeout");
                }
                break;
            case "spawn":
                using (var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }))
                    proc!.WaitForExit(5000);
                break;
            case "reg":
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Seekbeam-sandbox-probe-" + Guid.NewGuid().ToString("N"), true))
                    if (k == null) throw new UnauthorizedAccessException("CreateSubKey returned null");
                break;
            default:
                throw new ArgumentException("unknown command");
        }
        result = "OK";
    }
    catch (Exception ex)
    {
        var e = ex is AggregateException a && a.InnerException != null ? a.InnerException : ex;
        result = "DENIED " + e.GetType().Name;
    }
    stdout.WriteLine(result);
}
