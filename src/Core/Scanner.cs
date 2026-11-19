using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MagentaAddOnManager.Core;

/// <summary>How we decided what an add-on is.</summary>
public enum ClassificationSource
{
    Manifest,      // content_type read from manifest.json — authoritative
    Structure,     // inferred from folder contents, manifest missing/unusable
    Unclassified   // we could not tell; reported honestly, never guessed
}

public enum AddOnKind
{
    Aircraft, Scenery, Livery, Instrument, Utility, Other, Unknown
}

public sealed record AddOn(
    string FolderName,
    string FullPath,
    AddOnKind Kind,
    string? Title,
    string? ContentType,       // raw value as the sim declares it
    ClassificationSource Source,
    bool IsActive,
    long SizeBytes,
    string? Note               // why classification is uncertain, if it is
);

public static class Scanner
{
    /// <summary>
    /// Finds the Community folder by reading InstalledPackagesPath from UserCfg.opt.
    /// This is the only reliable method — the folder is often moved off C:.
    /// </summary>
    public static string? FindCommunityFolder()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"), // MS Store 2024
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft Flight Simulator 2024", "UserCfg.opt")                              // Steam 2024
        };

        foreach (var cfg in candidates)
        {
            if (!File.Exists(cfg)) continue;
            foreach (var raw in File.ReadAllLines(cfg))
            {
                var line = raw.Trim();
                const string key = "InstalledPackagesPath";
                if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

                // Format: InstalledPackagesPath "D:\MSFS Content"
                var q1 = line.IndexOf('"');
                var q2 = line.LastIndexOf('"');
                if (q1 < 0 || q2 <= q1) continue;

                var packages = line.Substring(q1 + 1, q2 - q1 - 1).Trim();
                if (packages.Length == 0) continue;

                var community = Path.Combine(packages, "Community");
                if (Directory.Exists(community)) return community;
            }
        }
        return null;
    }

    /// <summary>
    /// Reads one manifest.json. Never throws: a malformed file, a file locked by
    /// an installer mid-write, or an access denial all come back as a reported
    /// failure, because a scan must survive the folder changing under it.
    /// </summary>
    private static (string? contentType, string? title, string? fail) TryReadManifest(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        try
        {
            if (!File.Exists(path)) return (null, null, "no manifest.json");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string? ct = root.TryGetProperty("content_type", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            string? ti = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            return (ct, ti, null);
        }
        catch (JsonException)
        {
            return (null, null, "manifest.json is not valid JSON");
        }
        catch (IOException)
        {
            return (null, null, "manifest.json is locked by another program");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null, "manifest.json cannot be read (access denied)");
        }
    }

    private static AddOnKind MapContentType(string ct) => ct switch
    {
        "AIRCRAFT" => AddOnKind.Aircraft,
        "SCENERY" => AddOnKind.Scenery,
        "LIVERY" => AddOnKind.Livery,
        "INSTRUMENTS" => AddOnKind.Instrument,
        "MISC" => AddOnKind.Utility,
        "UNKNOWN" => AddOnKind.Unknown,
        _ => AddOnKind.Other   // e.g. "Sound Set" — real, but not one of our headings
    };

    /// <summary>Structural fallback: manifest missing, but the shape still tells us something.</summary>
    private static (AddOnKind, string?) ClassifyByStructure(string dir)
    {
        if (Directory.Exists(Path.Combine(dir, "SimObjects", "Airplanes")))
            return (AddOnKind.Aircraft, "found SimObjects/Airplanes");
        if (Directory.Exists(Path.Combine(dir, "scenery")))
            return (AddOnKind.Scenery, "found scenery/");
        return (AddOnKind.Unknown, null);
    }

    // Sizes are cached for the process lifetime. Without this, every UI refresh
    // re-walked the whole tree: with 20 packages over ~21 GB that is thousands of
    // file stats per click. A package's size does not change while the app runs.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
        SizeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Byte size of a package, measured once and cached.</summary>
    public static long SizeBytes(string dir)
    {
        if (SizeCache.TryGetValue(dir, out var cached)) return cached;

        long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }

        SizeCache[dir] = bytes;
        return bytes;
    }

    /// <summary>
    /// Drops cached sizes. Called on an explicit rescan: an add-on updated on
    /// disk must not keep showing its old size for the rest of the session.
    /// </summary>
    public static void InvalidateSizes() => SizeCache.Clear();

    /// <summary>
    /// Enumerates subdirectories without dying when the folder changes under us
    /// (an installer removing a package mid-scan). Keeps what was read before the
    /// failure instead of throwing the whole scan away.
    /// </summary>
    private static List<string> SafeDirs(string path)
    {
        var list = new List<string>();
        try
        {
            using var e = Directory.EnumerateDirectories(path).GetEnumerator();
            while (true)
            {
                try { if (!e.MoveNext()) break; }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
                list.Add(e.Current);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Human-readable size; cached after the first measurement.</summary>
    public static string ReadableSize(string dir) => ReadableSize(SizeBytes(dir));

    public static string ReadableSize(long bytes)
    {
        string[] unit = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < unit.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {unit[u]}" : $"{v:0.0} {unit[u]}";
    }

    /// <summary>
    /// Lists top-level packages in a Community folder. Active = present as a real
    /// directory; inactive ones live outside Community so the sim never sees them.
    /// </summary>
    public static List<AddOn> Scan(string community, string? inactive = null)
    {
        var results = new List<AddOn>();
        if (!Directory.Exists(community)) return results;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in SafeDirs(community))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".")) continue;
            seen.Add(name);

            var (ct, title, fail) = TryReadManifest(dir);
            AddOnKind kind;
            ClassificationSource source;
            string? note = fail;

            if (ct is not null)
            {
                kind = MapContentType(ct);
                source = ClassificationSource.Manifest;
            }
            else
            {
                (kind, var why) = ClassifyByStructure(dir);
                source = kind != AddOnKind.Unknown
                    ? ClassificationSource.Structure
                    : ClassificationSource.Unclassified;
                note = why is null ? (fail ?? "could not determine type") : $"{fail}; {why}";
            }

            // Size stays 0 here on purpose: walking every file of a multi-GB scenery
            // package just to render a number is wasteful. The UI asks for it lazily,
            // only for the rows actually on screen.
            results.Add(new AddOn(name, dir, kind, title, ct, source, true, 0, note));
        }

        // Every package leads from Inactive: that is where the real folder always
        // lives. Its presence in Community decides whether it is active.
        //
        // Earlier this also listed Inactive separately, double-counting every
        // add-on (7 aircraft reported as 14). One package, one row, always.
        if (inactive is not null && Directory.Exists(inactive))
        {
            foreach (var dir in SafeDirs(inactive))
            {
                var name = Path.GetFileName(dir);

                // already reported above (junction present in Community)
                if (seen.Contains(name)) continue;

                var (ct, title, _) = TryReadManifest(dir);
                var kind = ct is not null ? MapContentType(ct) : ClassifyByStructure(dir).Item1;
                results.Add(new AddOn(name, dir, kind, title, ct,
                    ct is not null ? ClassificationSource.Manifest : ClassificationSource.Structure,
                    false, 0, null));
            }
        }

        return results.OrderBy(a => a.FolderName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
