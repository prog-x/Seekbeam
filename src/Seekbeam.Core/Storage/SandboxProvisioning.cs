// FILE IMMUTABILITY POLICY: one-time set-up of the extractor sandbox (TD-17). Two Seekbeam-owned, OS-managed objects are
// written, and NEVER inside the indexing perimeter (the caller passes the perimeter test; anything in scope is refused):
//   1. the AppContainer profile "Seekbeam.ExtractorHost" (Windows creates %LOCALAPPDATA%\Packages\seekbeam.extractorhost and
//      a registry mapping under HKCU\Software\Classes\Local Settings) — required by CreateProcess for an AppContainer;
//   2. a read+execute ACE for ALL APPLICATION PACKAGES (S-1-15-2-1) on the directory that contains
//      Seekbeam.ExtractorHost.exe (Seekbeam's own binaries), so the AppContainer can load its own code.
// Both are skipped when already present; if either is refused, the extractor falls back to the low-integrity sandbox.
#pragma warning disable RS0030 // DirectoryInfo ACL read/write on Seekbeam's own program directory (reviewed, FILE_IMMUTABILITY_AUDIT.md).
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Seekbeam.Core.Diagnostics;

namespace Seekbeam.Core.Storage;

public enum SandboxProvisioningStatus
{
    /// <summary>Profile and ACL in place: the AppContainer sandbox can be used.</summary>
    Ready,
    /// <summary>The program directory is inside (or contains) an indexed root: its ACL is never changed.</summary>
    ProgramDirectoryInScope,
    /// <summary>%LOCALAPPDATA%\Packages is inside the indexing perimeter: the profile is never created there.</summary>
    ProfileLocationInScope,
    /// <summary>Windows refused the change (no WRITE_DAC on the program directory, policy, older OS…).</summary>
    Failed,
}

public static partial class SandboxProvisioning
{
    public const string AppContainerName = "Seekbeam.ExtractorHost";
    private static readonly SecurityIdentifier AllAppPackages = new("S-1-15-2-1");

    public static string PackagesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    public static string ProfileDirectory => Path.Combine(PackagesDirectory, AppContainerName.ToLowerInvariant());

    /// <summary>
    /// Ensures the AppContainer profile exists and <paramref name="programDirectory"/> is readable by AppContainers.
    /// <paramref name="touchesIndexedScope"/> must return true for a path that is inside the indexing perimeter or contains
    /// an indexed root: such a location is never written.
    /// </summary>
    /// <param name="dataRoot">
    /// Seekbeam's data directory. In portable mode it lies inside the program directory: it is then cut off from the
    /// inherited AppContainer ACE, so that no AppContainer (the extractor, or any other sandboxed app) can read the index.
    /// </param>
    public static SandboxProvisioningStatus Ensure(string programDirectory, Func<string, bool> touchesIndexedScope, string? dataRoot = null)
    {
        programDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programDirectory));
        try
        {
            if (dataRoot != null)
            {
                dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
                if (IsSameOrUnder(programDirectory, dataRoot))
                {
                    // The program directory is the data directory or inside it: an inheritable ACE would expose the index.
                    Log.Warn("Sandbox", "Program directory is inside the data directory; AppContainer ACL not added");
                    return SandboxProvisioningStatus.Failed;
                }
                // Before any grant, so the index is never exposed, even for a moment.
                if (IsSameOrUnder(dataRoot, programDirectory) && Directory.Exists(dataRoot)) ShieldFromAppContainers(dataRoot);
            }
            if (!HasAppContainerRead(programDirectory))
            {
                if (touchesIndexedScope(programDirectory))
                {
                    Log.Info("Sandbox", "Program directory is inside the indexing perimeter; AppContainer ACL not added");
                    return SandboxProvisioningStatus.ProgramDirectoryInScope;
                }
                GrantAppContainerRead(programDirectory);
                Log.Info("Sandbox", "Granted AppContainer read access to the Seekbeam program directory");
            }
            if (!Directory.Exists(ProfileDirectory))
            {
                if (touchesIndexedScope(PackagesDirectory) || touchesIndexedScope(ProfileDirectory))
                {
                    Log.Info("Sandbox", "%LOCALAPPDATA%\\Packages is inside the indexing perimeter; AppContainer profile not created");
                    return SandboxProvisioningStatus.ProfileLocationInScope;
                }
                int hr = CreateAppContainerProfile(AppContainerName, "Seekbeam extractor", "Sandbox for Seekbeam document text extraction",
                    IntPtr.Zero, 0, out IntPtr sid);
                if (sid != IntPtr.Zero) FreeSid(sid);
                if (hr != 0 && hr != unchecked((int)0x800700B7) /*already exists*/)
                {
                    Log.Warn("Sandbox", $"CreateAppContainerProfile failed (0x{hr:X8})");
                    return SandboxProvisioningStatus.Failed;
                }
                Log.Info("Sandbox", "AppContainer profile created");
            }
            return SandboxProvisioningStatus.Ready;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SystemException)
        {
            Log.Warn("Sandbox", "Sandbox provisioning failed: " + ex.Message);
            return SandboxProvisioningStatus.Failed;
        }
    }

    /// <summary>Removes the AppContainer profile (--purge-data). The program directory ACL goes away with the directory on uninstall.</summary>
    public static void RemoveProfile()
    {
        int hr = DeleteAppContainerProfile(AppContainerName);
        if (hr != 0) Log.Info("Sandbox", $"DeleteAppContainerProfile: 0x{hr:X8}");
    }

    /// <summary>True if AppContainers already have inheritable read+execute access on the directory.</summary>
    public static bool HasAppContainerRead(string directory)
    {
        var acl = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        bool Has(SecurityIdentifier sid) => acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r =>
            r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow
            && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute
            && (r.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        return Has(AllAppPackages);
    }

    /// <summary>True if the directory, or any file or folder below it, lets AppContainers in (explicit or inherited ACE).</summary>
    public static bool IsReadableByAppContainers(string directory)
    {
        bool Grants(FileSystemSecurity acl) => acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(AllAppPackages) && r.AccessControlType == AccessControlType.Allow);
        var di = new DirectoryInfo(directory);
        if (Grants(di.GetAccessControl(AccessControlSections.Access))) return true;
        foreach (var f in di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            FileSystemSecurity acl = f is DirectoryInfo d ? d.GetAccessControl(AccessControlSections.Access) : ((FileInfo)f).GetAccessControl(AccessControlSections.Access);
            if (Grants(acl)) return true;
        }
        return false;
    }

    /// <summary>
    /// Stops the AppContainer ACE of the program directory from reaching the data directory (portable mode): inheritance
    /// is blocked at the data directory (the other inherited ACEs are kept as explicit ones) and any AppContainer ACE removed.
    /// The change propagates to the existing database, index and log files.
    /// </summary>
    private static void ShieldFromAppContainers(string dataRoot)
    {
        var di = new DirectoryInfo(dataRoot);
        var acl = di.GetAccessControl(AccessControlSections.Access);
        bool exposed = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Equals(AllAppPackages));
        if (!exposed && acl.AreAccessRulesProtected) return;
        if (!acl.AreAccessRulesProtected)
        {
            // Inherited ACEs only become explicit once the protected descriptor is written: write it, then re-read.
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            di.SetAccessControl(acl);
            acl = di.GetAccessControl(AccessControlSections.Access);
        }
        acl.PurgeAccessRules(AllAppPackages);
        di.SetAccessControl(acl);
        Log.Info("Sandbox", "Data directory shielded from AppContainers (portable mode)");
    }

    private static bool IsSameOrUnder(string path, string dir) =>
        path.Equals(dir, StringComparison.OrdinalIgnoreCase)
        || (path.Length > dir.Length && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && path[dir.Length] == '\\');

    private static void GrantAppContainerRead(string directory)
    {
        var di = new DirectoryInfo(directory);
        var acl = di.GetAccessControl(AccessControlSections.Access);
        foreach (var sid in new[] { AllAppPackages })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(acl); // inheritable ACEs propagate to the existing files of the directory
    }

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint count, out IntPtr sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeleteAppContainerProfile(string name);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr FreeSid(IntPtr sid);
}
