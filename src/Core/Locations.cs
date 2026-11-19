using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MagentaAddOnManager.Core;

public enum SimEdition { Unknown, Msfs2024Store, Msfs2024Steam, Msfs2020Store, Msfs2020Steam }

public sealed record CommunityLocation(
    string Path,
    SimEdition Edition,
    string Source           // how we found it: "UserCfg.opt (Steam 2024)", "user selected", ...
);

/// <summary>
/// Finds the Community folder across sim versions and stores, without hardcoding
/// anyone's machine. Strategy, in order:
///   1. every known UserCfg.opt -> InstalledPackagesPath  (authoritative; covers moved folders)
///   2. the documented defaults                            (covers a fresh install never launched)
///   3. the user picks it                                  (covers anything else)
/// Nothing here assumes a drive letter or username.
/// </summary>
public static class Locations
{
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>All UserCfg.opt locations we know of, each tagged with its edition.</summary>
    private static IEnumerable<(string Cfg, SimEdition Edition)> KnownConfigs()
    {
        // MSFS 2024 — Microsoft Store / Xbox
        yield return (Path.Combine(LocalAppData, "Packages",
            "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"), SimEdition.Msfs2024Store);
        // MSFS 2024 — Steam
        yield return (Path.Combine(AppData, "Microsoft Flight Simulator 2024", "UserCfg.opt"),
            SimEdition.Msfs2024Steam);
        // MSFS 2020 — Microsoft Store / Xbox
        yield return (Path.Combine(LocalAppData, "Packages",
            "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"), SimEdition.Msfs2020Store);
        // MSFS 2020 — Steam
        yield return (Path.Combine(AppData, "Microsoft Flight Simulator", "UserCfg.opt"),
            SimEdition.Msfs2020Steam);
    }

    private static string? ReadInstalledPackagesPath(string cfg)
    {
        if (!File.Exists(cfg)) return null;
        try
        {
            foreach (var raw in File.ReadAllLines(cfg))
            {
                var line = raw.Trim();
                if (!line.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
                var q1 = line.IndexOf('"');
                var q2 = line.LastIndexOf('"');
                if (q1 < 0 || q2 <= q1) continue;
                var v = line.Substring(q1 + 1, q2 - q1 - 1).Trim();
                if (v.Length > 0) return v;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// All Community folders we can detect, best-first. Includes Community2024
    /// alongside Community, because MSFS 2024 reads both.
    /// </summary>
    public static List<CommunityLocation> DetectAll()
    {
        var found = new List<CommunityLocation>();

        foreach (var (cfg, edition) in KnownConfigs())
        {
            var packages = ReadInstalledPackagesPath(cfg);
            if (packages is null) continue;

            foreach (var folder in new[] { "Community", "Community2024" })
            {
                var candidate = Path.Combine(packages, folder);
                if (Directory.Exists(candidate))
                    found.Add(new CommunityLocation(candidate, edition, $"UserCfg.opt ({EditionLabel(edition)})"));
            }
        }

        // Documented defaults, for an install that has never been launched.
        foreach (var (dir, edition) in DefaultFolders())
        {
            if (Directory.Exists(dir) &&
                !found.Any(f => string.Equals(f.Path, dir, StringComparison.OrdinalIgnoreCase)))
                found.Add(new CommunityLocation(dir, edition, $"default ({EditionLabel(edition)})"));
        }

        return found;
    }

    private static IEnumerable<(string, SimEdition)> DefaultFolders()
    {
        yield return (Path.Combine(AppData, "Microsoft Flight Simulator 2024", "Packages", "Community"),
            SimEdition.Msfs2024Steam);
        yield return (Path.Combine(LocalAppData, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe",
            "LocalCache", "Packages", "Community"), SimEdition.Msfs2024Store);
        yield return (Path.Combine(AppData, "Microsoft Flight Simulator", "Packages", "Community"),
            SimEdition.Msfs2020Steam);
        yield return (Path.Combine(LocalAppData, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe",
            "LocalCache", "Packages", "Community"), SimEdition.Msfs2020Store);
    }

    /// <summary>The best single location, or null so the UI can ask the user.</summary>
    public static CommunityLocation? DetectPrimary()
    {
        var all = DetectAll();
        // Prefer 2024; the 2020-only user gets their 2020 folder either way.
        return all
            .OrderBy(l => l.Edition is SimEdition.Msfs2024Store or SimEdition.Msfs2024Steam ? 0 : 1)
            .ThenBy(l => l.Path.EndsWith("Community2024", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
    }

    public static string EditionLabel(SimEdition e) => e switch
    {
        SimEdition.Msfs2024Store => "MSFS 2024 — Microsoft Store",
        SimEdition.Msfs2024Steam => "MSFS 2024 — Steam",
        SimEdition.Msfs2020Store => "MSFS 2020 — Microsoft Store",
        SimEdition.Msfs2020Steam => "MSFS 2020 — Steam",
        _ => "unknown"
    };
}
