using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MagentaAddOnManager.Core;

/// <summary>
/// Enables and disables add-ons using NTFS directory junctions.
///
/// State model — the real content ALWAYS lives in Inactive:
///   active   = Community\name is a junction -> Inactive\name
///   inactive = no junction in Community; folder remains Inactive\name
///
/// Consequences worth knowing:
///  - enabling/disabling is create/delete of a junction: instant at any size
///  - the sim only ever enumerates junctions we put in Community, so a disabled
///    add-on is invisible to it (it sits outside Community entirely)
///  - migrating existing real folders into Inactive is a same-volume move: instant
/// </summary>
public sealed class JunctionManager
{
    public string Community { get; }
    public string Inactive { get; }

    public JunctionManager(string community, string? inactive = null)
    {
        Community = community;

        // Default: a sibling of Community, so moves stay same-volume and instant.
        // Configurable because some users keep Community on a small or read-only
        // volume and need their library elsewhere. Cross-volume still works, it
        // just costs a copy on the first migration instead of an instant move.
        var parent = Path.GetDirectoryName(community.TrimEnd('\\')) ?? community;
        Inactive = inactive ?? Path.Combine(parent, "Inactive");
    }

    public void EnsureInactiveFolder()
    {
        if (!Directory.Exists(Inactive)) Directory.CreateDirectory(Inactive);
    }

    /// <summary>True if the path exists as a directory junction / reparse point.</summary>
    public static bool IsJunction(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// One-time migration: today's real folders in Community become
    /// Inactive real folders + Community junctions. Skips anything already done.
    /// Returns names migrated.
    /// </summary>
    public int MigrateExisting(Action<string>? log = null)
    {
        EnsureInactiveFolder();
        var migrated = 0;

        foreach (var dir in Directory.EnumerateDirectories(Community))
        {
            var name = Path.GetFileName(dir);

            // already migrated: it's a junction pointing at our Inactive folder
            if (IsJunction(dir)) continue;

            var dest = Path.Combine(Inactive, name);
            if (Directory.Exists(dest))
            {
                // both a real folder here and one in Inactive: don't guess, don't merge
                log?.Invoke($"skipped '{name}': real folder in both Community and Inactive");
                continue;
            }

            Directory.Move(dir, dest);                 // same volume: instant
            Junction.Create(dir, dest);
            log?.Invoke($"migrated '{name}'");
            migrated++;
        }

        return migrated;
    }

    /// <summary>Make Community\name load: ensure junction exists.</summary>
    public void Enable(string name)
    {
        EnsureInactiveFolder();
        var junctionPath = Path.Combine(Community, name);
        var realPath = Path.Combine(Inactive, name);

        if (!Directory.Exists(realPath))
            throw new InvalidOperationException($"cannot enable '{name}': no folder in Inactive");

        if (Directory.Exists(junctionPath))
        {
            if (Junction.IsJunction(junctionPath))
            {
                // A junction pointing somewhere else belongs to another tool
                // (Addon Linker, a manual link). Never claim it as ours.
                var target = Junction.GetTarget(junctionPath);
                if (target is not null && PathsEqual(target, realPath)) return; // already enabled
                throw new InvalidOperationException(
                    $"'{name}' is linked to '{target ?? "an unknown location"}', not to this app's Inactive folder. " +
                    "Manage it with the tool that created the link.");
            }
            throw new InvalidOperationException(
                $"cannot enable '{name}': a real folder blocks the junction path");
        }

        Junction.Create(junctionPath, realPath);
    }

    /// <summary>
    /// Stop Community\name loading.
    ///
    /// Two cases, both ordinary:
    ///  - a junction (already managed) -> delete the link, content untouched
    ///  - a real folder (a brand-new user) -> move it into Inactive
    ///
    /// The second case is why there is no upfront migration step any more. A new
    /// user's add-ons are plain folders; disabling one simply parks it. Nothing
    /// gets restructured until they actually ask for it, and nothing errors on
    /// the first click.
    /// </summary>
    public void Disable(string name)
    {
        var inCommunity = Path.Combine(Community, name);
        if (!Directory.Exists(inCommunity)) return;            // already disabled

        if (Junction.IsJunction(inCommunity))
        {
            // Only remove links that point into OUR Inactive folder. A junction
            // created by Addon Linker or by hand points at someone else's library;
            // removing it would orphan the add-on from both tools' views.
            var target = Junction.GetTarget(inCommunity);
            if (target is null || !IsUnderInactive(target))
                throw new InvalidOperationException(
                    $"'{name}' is linked to '{target ?? "an unknown location"}', outside this app's Inactive folder. " +
                    "Manage it with the tool that created the link.");

            Junction.Remove(inCommunity);
            return;
        }

        EnsureInactiveFolder();
        var dest = Path.Combine(Inactive, name);
        if (Directory.Exists(dest))
            throw new InvalidOperationException(
                $"cannot disable '{name}': another copy already sits in Inactive. Move one of them out first.");

        MoveDirectory(inCommunity, dest);
    }

    private bool IsUnderInactive(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var root = Path.GetFullPath(Inactive).TrimEnd('\\');
        return full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
            || PathsEqual(full, root);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                      Path.GetFullPath(b).TrimEnd('\\'),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Directory.Move is instant on one volume and throws across two. Fall back to
    /// a copy only when the volumes genuinely differ, so a cross-volume library
    /// still works — it just costs time on the first toggle.
    /// </summary>
    private static void MoveDirectory(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
            return;
        }
        catch (IOException)
        {
            // Different volume, or the move is otherwise impossible. Copy instead.
        }

        CopyDirectory(from, to);
        try { Directory.Delete(from, recursive: true); }
        catch (Exception ex)
        {
            throw new IOException(
                $"copied to '{to}' but could not remove the original at '{from}': {ex.Message}", ex);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: false);
    }

    /// <summary>
    /// Reports exactly what MigrateExisting would do, changing nothing.
    /// Lets us show the plan — and let the user confirm — before touching real data.
    /// </summary>
    public List<(string Name, string Action, string Reason)> DryRun()
    {
        var plan = new List<(string, string, string)>();
        if (!Directory.Exists(Community)) return plan;

        foreach (var dir in Directory.EnumerateDirectories(Community))
        {
            var name = Path.GetFileName(dir);
            if (Junction.IsJunction(dir))
            {
                plan.Add((name, "skip", "already a junction"));
            }
            else if (Directory.Exists(Path.Combine(Inactive, name)))
            {
                plan.Add((name, "SKIP", "real folder in BOTH Community and Inactive — needs a human"));
            }
            else
            {
                plan.Add((name, "move + link",
                    $"{Scanner.ReadableSize(dir)} -> Inactive, junction back"));
            }
        }
        return plan;
    }

}
