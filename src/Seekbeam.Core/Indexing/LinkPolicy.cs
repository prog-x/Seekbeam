using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Indexing;

/// <summary>
/// Decides whether a reparse-point directory (symlink, junction, mount point) is traversed.
/// Default: never followed (the link itself is indexed). When following is enabled for a root, a link is followed only
/// if its final target (1) exists, (2) is not inside any configured root (it is indexed there already — avoids
/// duplicates), (3) is not an ancestor of the directory containing the link (cycle), and (4) depth stays bounded.
/// Cloud-file reparse points (OneDrive…) are ordinary directories and always traversed.
/// </summary>
public static class LinkPolicy
{
    public const int MaxDepthWhenFollowing = 64;

    public static bool IsTraversableDirectory(uint reparseTag, FileAttributes attrs)
    {
        if ((attrs & FileAttributes.ReparsePoint) == 0) return true;
        return ReparseTags.IsCloud(reparseTag);
    }

    public static bool ShouldFollow(RootScope root, ScopeRules scope, string linkPath, string containingDirPath, int depth, out string? reason)
    {
        reason = null;
        if (!root.Config.FollowLinks) { reason = "links not followed for this root"; return false; }
        if (depth >= MaxDepthWhenFollowing) { reason = "max depth"; return false; }
        string? target = SourceFileAccess.TryGetFinalPath(linkPath);
        if (target == null) { reason = "dangling link"; return false; }
        target = Path.TrimEndingDirectorySeparator(target);
        // Only the locations count: a folder-index root (a whole drive) records folder names, not the files behind the link.
        if (scope.FindLocation(target) != null) { reason = "target already inside an indexed location"; return false; }
        string? containerFinal = SourceFileAccess.TryGetFinalPath(containingDirPath);
        if (containerFinal != null)
        {
            containerFinal = Path.TrimEndingDirectorySeparator(containerFinal);
            if (containerFinal.Equals(target, StringComparison.OrdinalIgnoreCase)
                || containerFinal.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
            { reason = "cycle (target is an ancestor)"; return false; }
        }
        return true;
    }
}
