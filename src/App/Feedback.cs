using System;
using System.IO;
using System.Linq;
using System.Text;
using MagentaAddOnManager.Core;

namespace MagentaAddOnManager;

/// <summary>
/// Builds a feedback report and the links that send it. The report is shown to
/// the user verbatim before anything is sent — no silent telemetry. Diagnostics
/// are the facts that make a bug report actionable: version, OS build, sim
/// edition, paths, counts, and the tail of the error log.
/// </summary>
public static class Feedback
{
    public const string Email = "alan@magentadebrief.com";

    // The public repo. The GitHub feedback button activates because this is set.
    public const string GitHubRepoUrl = "https://github.com/flyingsotong/magenta-addon-manager";

    public static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.3.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string BuildReport(
        string message,
        CommunityLocation? location,
        JunctionManager? jm,
        int active, int total)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Magenta Add-On Manager feedback");
        sb.AppendLine($"Version: {AppVersion}");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm} (local)");
        sb.AppendLine($"Windows: {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($"Sim: {(location is null ? "folder chosen by hand" : Locations.EditionLabel(location.Edition))}");
        if (jm is not null)
        {
            sb.AppendLine($"Community: {jm.Community}");
            sb.AppendLine($"Inactive: {jm.Inactive}");
        }
        sb.AppendLine($"Add-ons: {active} active of {total}");
        sb.AppendLine($"Theme: {(Theme.IsLight ? "Light" : "Dark")}");
        sb.AppendLine();
        sb.AppendLine("--- message ---");
        sb.AppendLine(string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim());
        sb.AppendLine();
        sb.AppendLine("--- recent errors (error.log tail) ---");
        sb.AppendLine(ErrorLogTail());
        return sb.ToString();
    }

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Magenta Add-On Manager");

    private static string ErrorLogTail()
    {
        try
        {
            var path = Path.Combine(SettingsDir, "error.log");
            if (!File.Exists(path)) return "(none)";
            var lines = File.ReadAllLines(path);
            var tail = lines.Skip(Math.Max(0, lines.Length - 20))
                            .Select(l => l.Length > 200 ? l[..200] + "…" : l);
            var text = string.Join("\n", tail).Trim();
            return text.Length == 0 ? "(none)" : text;
        }
        catch { return "(could not read error.log)"; }
    }

    public static string MailtoUrl(string report)
    {
        var subject = Uri.EscapeDataString($"Magenta Add-On Manager feedback (v{AppVersion})");
        var body = Uri.EscapeDataString(report);
        return $"mailto:{Email}?subject={subject}&body={body}";
    }

    public static string GitHubIssueUrl(string report)
    {
        if (string.IsNullOrEmpty(GitHubRepoUrl)) return "";
        var title = Uri.EscapeDataString($"Feedback (v{AppVersion})");
        var body = Uri.EscapeDataString(report);
        return $"{GitHubRepoUrl.TrimEnd('/')}/issues/new?title={title}&body={body}";
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { /* opening a browser must never crash the app */ }
    }
}
