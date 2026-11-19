using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace MagentaAddOnManager;

/// <summary>
/// Theme switching by swapping one ResourceDictionary.
///
/// The brushes live in ThemeDark.xaml / ThemeLight.xaml and every consumer uses
/// DynamicResource, so replacing the merged dictionary re-resolves the whole
/// interface at once.
///
/// Earlier attempts mutated or replaced individual brush entries and all failed
/// for the same reason: brushes referenced by a StaticResource inside a sealed
/// ControlTemplate are frozen, and a Setter.Value cannot take DynamicResource.
/// Swapping the dictionary sidesteps both — the new brushes are fresh objects and
/// the templates are re-templated against them.
///
/// Palette values come from DSH's design platform
/// (@deepseek-ai/dsh-client-ui-theme, design-platform.css), not invented here.
/// This file therefore holds no colour values at all: one source of truth.
/// </summary>
public static class Theme
{
    public static bool IsLight { get; private set; }

    private const string DarkSource = "ThemeDark.xaml";
    private const string LightSource = "ThemeLight.xaml";

    public static void Apply(bool light)
    {
        IsLight = light;
        var app = Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;
        var wanted = light ? LightSource : DarkSource;

        var replacement = new ResourceDictionary
        {
            Source = new Uri(wanted, UriKind.Relative)
        };

        for (var i = 0; i < dicts.Count; i++)
        {
            var source = dicts[i].Source?.OriginalString ?? "";
            if (source.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase))
            {
                dicts[i] = replacement;
                return;
            }
        }

        // No theme dictionary present yet (first run): put it in front.
        dicts.Insert(0, replacement);
    }

    /// <summary>Whether the one-time explanation has been dismissed.</summary>
    public static bool ExplainDismissed { get; set; }

    // ---- persistence ----------------------------------------------------

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Magenta Add-On Manager", "settings.json");

    private sealed class Settings
    {
        public bool LightTheme { get; set; }
        public bool ExplainDismissed { get; set; }
    }

    /// <summary>Reads every saved preference. Safe to call before any window exists.</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (s is null) return;
            IsLight = s.LightTheme;
            ExplainDismissed = s.ExplainDismissed;
        }
        catch { /* unreadable preferences fall back to defaults */ }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(
                    new Settings { LightTheme = IsLight, ExplainDismissed = ExplainDismissed },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a preference that cannot be saved must not break the app */ }
    }
}
