using MagentaAddOnManager.Core;

// Combined proof suite for the audit fixes. Sandbox only, plus one READ-ONLY
// check against the real Community folder's existing junctions.

int fail = 0;
void Check(string label, bool ok, string? d = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(d is null ? "" : " -> " + d)}");
    if (!ok) fail++;
}

var root = Path.Combine(Path.GetTempPath(), "magenta-auditfix-" + Guid.NewGuid().ToString("N")[..8]);
var community = Path.Combine(root, "MSFS Content", "Community");
Directory.CreateDirectory(community);
var jm = new JunctionManager(community);

void AddOn(string name, string ct)
{
    var d = Path.Combine(community, name);
    Directory.CreateDirectory(d);
    File.WriteAllText(Path.Combine(d, "manifest.json"), $"{{\"content_type\":\"{ct}\",\"title\":\"{name}\"}}");
}

Console.WriteLine("1. lazy toggle round-trip with the new junction engine");
AddOn("plane-a", "AIRCRAFT");
AddOn("field-x", "SCENERY");
jm.Disable("plane-a");
Check("real folder parked", File.Exists(Path.Combine(jm.Inactive, "plane-a", "manifest.json")));
Check("gone from Community", !Directory.Exists(Path.Combine(community, "plane-a")));
jm.Enable("plane-a");
Check("junction resolves", Junction.Resolves(Path.Combine(community, "plane-a")));
Check("manifest through junction", Junction.ContainsFile(Path.Combine(community, "plane-a"), "manifest.json"));
jm.Disable("plane-a");
Check("second disable removes link only", File.Exists(Path.Combine(jm.Inactive, "plane-a", "manifest.json")));
jm.Enable("plane-a");

Console.WriteLine("\n2. locked manifest no longer kills the scan");
AddOn("locked-pkg", "AIRCRAFT");
var mf = Path.Combine(community, "locked-pkg", "manifest.json");
using (var hold = new FileStream(mf, FileMode.Open, FileAccess.Read, FileShare.None))
{
    try
    {
        var scan = Scanner.Scan(community, jm.Inactive);
        var row = scan.First(a => a.FolderName == "locked-pkg");
        Check("scan survives", true, $"{scan.Count} packages");
        Check("locked pkg reported honestly", row.Note?.Contains("locked") == true, row.Note);
    }
    catch (Exception ex) { Check("scan survives", false, ex.GetType().Name); }
}

Console.WriteLine("\n3. foreign junctions are refused, not orphaned");
var foreignLib = Path.Combine(root, "someone-elses-library", "their-plane");
Directory.CreateDirectory(foreignLib);
File.WriteAllText(Path.Combine(foreignLib, "manifest.json"), "{\"content_type\":\"AIRCRAFT\"}");
var foreignLink = Path.Combine(community, "their-plane");
Junction.Create(foreignLink, foreignLib);
try { jm.Disable("their-plane"); Check("Disable refuses foreign link", false); }
catch (InvalidOperationException ex) { Check("Disable refuses foreign link", ex.Message.Contains("outside"), ex.Message.Split('.')[0]); }
Check("foreign link still present", Directory.Exists(foreignLink));
Check("foreign content untouched", File.Exists(Path.Combine(foreignLib, "manifest.json")));
try { jm.Enable("their-plane"); Check("Enable refuses foreign link", false); }
catch (InvalidOperationException) { Check("Enable refuses foreign link", true); }
Junction.Remove(foreignLink);

Console.WriteLine("\n4. size cache invalidation");
var sized = Path.Combine(jm.Inactive, "plane-a");
var before = Scanner.SizeBytes(sized);
File.WriteAllText(Path.Combine(sized, "extra.bin"), new string('x', 1024 * 1024));
Check("cache holds old size until invalidated", Scanner.SizeBytes(sized) == before);
Scanner.InvalidateSizes();
Check("rescan sees new size", Scanner.SizeBytes(sized) > before,
    $"{before} -> {Scanner.SizeBytes(sized)}");

Console.WriteLine("\n5. adversarial names still literal");
var weird = Path.Combine(community, "pkg%TEMP%&name");
Directory.CreateDirectory(weird);
File.WriteAllText(Path.Combine(weird, "manifest.json"), "{\"content_type\":\"MISC\"}");
jm.Disable("pkg%TEMP%&name");
Check("weird name parked literally", Directory.Exists(Path.Combine(jm.Inactive, "pkg%TEMP%&name")));
jm.Enable("pkg%TEMP%&name");
Check("weird name re-linked literally", Junction.Resolves(Path.Combine(community, "pkg%TEMP%&name")));

try { Directory.Delete(root, true); } catch { }

Console.WriteLine("\n6. READ-ONLY: existing live junctions (mklink-created) still recognised");
var realCommunity = @"D:\MSFS Content\Community";
var realInactive = @"D:\MSFS Content\Inactive";
if (Directory.Exists(realCommunity))
{
    var realJm = new JunctionManager(realCommunity);
    int recognised = 0, total = 0;
    foreach (var dir in Directory.EnumerateDirectories(realCommunity))
    {
        if (!Junction.IsJunction(dir)) continue;
        total++;
        var t = Junction.GetTarget(dir);
        if (t is not null && t.StartsWith(realInactive + "\\", StringComparison.OrdinalIgnoreCase))
            recognised++;
        else
            Console.WriteLine($"      NOT recognised: {Path.GetFileName(dir)} -> {t ?? "(null)"}");
    }
    Check($"all {total} live junctions point into Inactive", recognised == total, $"{recognised}/{total}");
}
else Console.WriteLine("  (real folder not present, skipped)");

Console.WriteLine($"\nresult: {(fail == 0 ? "ALL PASS" : fail + " FAILED")}");
return fail == 0 ? 0 : 1;
