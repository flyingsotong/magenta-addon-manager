using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MagentaAddOnManager.Core;

namespace MagentaAddOnManager;

public partial class MainWindow : Window
{
    private JunctionManager? _jm;
    private CommunityLocation? _location;
    private List<(string Name, bool WasActive)>? _undoSnapshot;
    private string _undoLabel = "";

    private string _filter = "";
    private string? _transientNote;   // shown once, after a rescan
    private HashSet<string> _lastNames = new(StringComparer.OrdinalIgnoreCase);
    private enum SortMode { Name, Size, Recent, Status }
    private SortMode _sort = SortMode.Name;

    public MainWindow()
    {
        InitializeComponent();
        LightThemeCheck.IsChecked = Theme.IsLight;
        Loaded += (_, _) => Start();
        PreviewKeyDown += Window_PreviewKeyDown;
        Activated += (_, _) => UpdateSimPanel();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Rescan();

    /// <summary>
    /// Re-reads the folder for add-ons added, removed or renamed since the list was
    /// built. Keeps the scroll position and says plainly what changed, because a
    /// silent rebuild would leave you unsure whether it found anything.
    /// </summary>
    private void Rescan()
    {
        if (_jm is null) { Start(); return; }

        // Diff against what is on screen, not against a fresh scan: the folder has
        // already changed by the time this runs, so two scans taken now would
        // always agree and the diff would always be empty.
        //
        // An explicit rescan also drops cached sizes: an add-on updated on disk
        // must not keep showing its old size for the rest of the session.
        Scanner.InvalidateSizes();
        var fresh = Scanner.Scan(_jm.Community, _jm.Inactive);
        var after = fresh.Select(a => a.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = after.Except(_lastNames, StringComparer.OrdinalIgnoreCase).Count();
        var removed = _lastNames.Except(after, StringComparer.OrdinalIgnoreCase).Count();

        // Set before rendering: UpdateStatus consumes the note as it draws.
        _transientNote = (added, removed) switch
        {
            (0, 0) => "no new add-ons",
            (1, 0) => "1 new add-on found",
            (_, 0) => $"{added} new add-ons found",
            (0, 1) => "1 add-on removed",
            (0, _) => $"{removed} add-ons removed",
            _ => $"{added} added, {removed} removed"
        };

        var offset = ListScroll.VerticalOffset;
        Render();

        // Restore the scroll after layout, otherwise the new extent is not measured
        // yet and the offset gets clamped to zero.
        Dispatcher.BeginInvoke(new Action(() => ListScroll.ScrollToVerticalOffset(offset)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LightTheme_Changed(object sender, RoutedEventArgs e)
    {
        Theme.Apply(LightThemeCheck.IsChecked == true);
        Theme.Save();

        // Rows and the status pills take their colours as local values resolved at
        // build time, so both are rebuilt to pick up the new palette. Missing this
        // left "FOLDER FOUND" invisible against the light background.
        UpdateSimPanel();
        UpdateFolderPill(_location is not null);
        Render();
    }

    // ---- window chrome -------------------------------------------------
    //
    // The compositor owns the title bar and its corners. A default light title
    // bar above a designed dark panel is exactly the mismatch this app avoids,
    // and WPF cannot style that bar itself.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
        ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        var hwnd = helper.Handle;
        int dark = 1;
        // 20 on Windows 11 / 10 2004+, 19 on older builds. Try both; harmless.
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, 4);

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);

        // Custom chrome does not constrain maximise by itself: Windows would size
        // the window to the whole screen, overlapping the taskbar and bleeding the
        // resize border past every edge. Measured 3860x2180 on a 3840x2088 work
        // area before this hook existed.
        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.rcWork;
            var mon = info.rcMonitor;

            mmi.ptMaxPosition.X = work.Left - mon.Left;
            mmi.ptMaxPosition.Y = work.Top - mon.Top;
            mmi.ptMaxSize.X = work.Right - work.Left;
            mmi.ptMaxSize.Y = work.Bottom - work.Top;

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void Start()
    {
        _location = Locations.DetectPrimary();
        if (_location is null) { ShowNoFolder(); return; }
        Bind(_location.Path, _location);
    }

    private void Bind(string community, CommunityLocation? loc)
    {
        var candidate = new JunctionManager(community);
        try
        {
            // A hand-picked folder may sit somewhere we cannot write (Program
            // Files, a read-only share). Fail with a message, not a crash, and
            // keep whatever was bound before.
            candidate.EnsureInactiveFolder();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Cannot use '{community}':\n\n{ex.Message}\n\n" +
                "Pick a folder you can write to — usually the Community folder inside your sim's Packages folder.",
                "Folder not usable", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_jm is null) ShowNoFolder();
            return;
        }

        _jm = candidate;

        PathLine.Text = community;
        EditionLabel.Text = loc is null
            ? "folder chosen by hand"
            : Locations.EditionLabel(loc.Edition);
        EditionLabel.ToolTip = loc is null
            ? "You picked this folder yourself"
            : "Found automatically from your simulator's UserCfg.opt";

        ExplainPanel.Visibility = Theme.ExplainDismissed
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateFolderPill(loc is not null);
        paintSortButtons();
        UpdateSimPanel();
        Render();

        // One source of truth for the version: the assembly, not a literal.
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionLine.Text = v is null ? "v0.2.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    private void Render()
    {
        Groups.Children.Clear();
        if (_jm is null) return;

        var all = Scanner.Scan(_jm.Community, _jm.Inactive);

        // Filter narrows what is shown, never what exists. Counts below stay honest
        // because they describe the whole folder, not the visible slice.
        var term = _filter.Trim();
        var visible = string.IsNullOrEmpty(term)
            ? all
            : all.Where(a => Matches(a, term)).ToList();

        var order = new[] { AddOnKind.Aircraft, AddOnKind.Scenery, AddOnKind.Livery,
                            AddOnKind.Instrument, AddOnKind.Utility, AddOnKind.Other, AddOnKind.Unknown };

        foreach (var kind in order)
        {
            var items = Sort(visible.Where(a => a.Kind == kind).ToList());
            if (items.Count == 0) continue;

            // Counts always describe the whole group, never just the visible slice.
            // Two different denominators on one screen would be ambiguous.
            var groupAll = all.Where(a => a.Kind == kind).ToList();
            var groupActive = groupAll.Count(i => i.IsActive);
            var countText = string.IsNullOrEmpty(term)
                ? $"{groupActive} OF {groupAll.Count} ACTIVE"
                : $"{groupActive} OF {groupAll.Count} ACTIVE \u00b7 SHOWING {items.Count}";

            var head = new DockPanel { Margin = new Thickness(0, 16, 0, 10), LastChildFill = false };

            // Section label: an amber rule, then an uppercase dim word. Same device
            // the bridge uses to open a section. Meta sits far right.
            var rule = new Border
            {
                Width = 14, Height = 2, CornerRadius = new CornerRadius(1),
                Background = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            DockPanel.SetDock(rule, Dock.Left);
            head.Children.Add(rule);

            var title = new TextBlock
            {
                Text = KindLabel(kind),
                Style = (Style)FindResource("SectionLabel"),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(title);

            var meta = new TextBlock
            {
                Text = countText,
                Style = (Style)FindResource("SectionMeta"),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(meta, Dock.Right);
            head.Children.Add(meta);

            // Group bulk controls sit to the left of the meta, still at the edge.
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(actions, Dock.Right);
            actions.Children.Add(MakeMini("All on", () => Bulk(items, true)));
            actions.Children.Add(MakeMini("All off", () => Bulk(items, false)));
            head.Children.Add(actions);

            Groups.Children.Add(head);

            foreach (var item in items)
                Groups.Children.Add(MakeRow(item));
        }

        if (visible.Count == 0 && all.Count > 0)
            Groups.Children.Add(EmptyState(term));

        _lastNames = all.Select(a => a.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UpdateStatus(all);
    }

    private static bool Matches(AddOn a, string term) =>
        (a.Title ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
        || a.FolderName.Contains(term, StringComparison.OrdinalIgnoreCase);

    private List<AddOn> Sort(List<AddOn> items) => _sort switch
    {
        SortMode.Size => items.OrderByDescending(a => Scanner.SizeBytes(RealPathOf(a))).ToList(),
        SortMode.Recent => items.OrderByDescending(a => ModifiedOf(a)).ToList(),
        // Loaded add-ons first, ignored ones after, alphabetical within each run.
        SortMode.Status => items.OrderBy(a => a.IsActive ? 0 : 1)
                               .ThenBy(a => a.Title ?? a.FolderName, StringComparer.OrdinalIgnoreCase)
                               .ToList(),
        _ => items.OrderBy(a => a.Title ?? a.FolderName, StringComparer.OrdinalIgnoreCase).ToList()
    };

    private string RealPathOf(AddOn a) =>
        _jm is null ? a.FullPath : Path.Combine(_jm.Inactive, a.FolderName);

    private static DateTime ModifiedOf(AddOn a)
    {
        try { return Directory.GetLastWriteTime(a.FullPath); }
        catch { return DateTime.MinValue; }
    }

    private UIElement EmptyState(string term)
    {
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8) };
        card.Child = new TextBlock
        {
            Text = string.IsNullOrEmpty(term)
                ? "Your Community folder is empty. Add something and reopen this window."
                : $"Nothing matches \u201c{term}\u201d.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("Body")
        };
        return card;
    }

    private UIElement MakeRow(AddOn item)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 8),
            // A faded row on white loses contrast faster than on dark: 0.45 puts
            // inactive text near 3:1, so light mode fades less.
            Opacity = item.IsActive ? 1.0 : (Theme.IsLight ? 0.62 : 0.45),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = (Brush)FindResource("SurfaceBrush")
        };

        var dock = new DockPanel();

        // The checkbox is the indicator and the accessible control. The whole card
        // is also clickable (a 15px square is fiddly to hit), via the card's mouse
        // handler below. The box itself is IsHitTestVisible=false so a mouse click
        // passes through to the card and never double-fires; but its Checked /
        // Unchecked events ARE hooked, so keyboard-on-box, screen-reader and
        // programmatic toggles all work. Leaving them unhooked silently broke
        // every non-mouse path.
        var cb = new CheckBox
        {
            Style = (Style)FindResource("StateCheckbox"),
            IsChecked = item.IsActive,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(cb, item.Title ?? item.FolderName);
        cb.Checked += (_, _) => SetState(item, true);
        cb.Unchecked += (_, _) => SetState(item, false);
        DockPanel.SetDock(cb, Dock.Left);
        dock.Children.Add(cb);

        var size = new TextBlock
        {
            Text = SizeOf(item),
            Style = (Style)FindResource("Small"),
            MinWidth = 60, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(size, Dock.Right);
        dock.Children.Add(size);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item.Title ?? item.FolderName,
            Style = (Style)FindResource("Body"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        meta.Children.Add(new TextBlock
        {
            Text = item.IsActive ? "Active" : "Inactive",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)(item.IsActive ? FindResource("GoodBrush") : FindResource("DimBrush"))
        });
        if (item.Note is not null)
            meta.Children.Add(new TextBlock
            {
                Text = item.Note, Style = (Style)FindResource("Small"),
                Margin = new Thickness(10, 0, 0, 0)
            });
        text.Children.Add(meta);
        dock.Children.Add(text);

        card.Child = dock;
        card.MouseLeftButtonUp += (_, _) => SetState(item, !item.IsActive);
        card.ToolTip = item.IsActive
            ? "Loaded by the sim — click to turn off"
            : "Not loaded — click to turn on";

        // Keyboard: every row is reachable by Tab and toggled with Space or Enter.
        // Focus is shown as a lighter fill, never an outline — borders mean
        // container in this design, so state is carried by fill.
        card.Focusable = true;
        card.KeyDown += (_, k) =>
        {
            if (k.Key == System.Windows.Input.Key.Space || k.Key == System.Windows.Input.Key.Enter)
            {
                SetState(item, !item.IsActive);
                k.Handled = true;
            }
        };
        card.GotKeyboardFocus += (_, _) =>
            card.Background = (Brush)FindResource("SurfaceHover");
        card.LostKeyboardFocus += (_, _) =>
            card.Background = (Brush)FindResource("SurfaceBrush");

        return card;
    }

    private static Button MakeMini(string label, Action click)
    {
        var b = new Button
        {
            Content = label,
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11
        };
        b.Click += (_, _) => click();
        return b;
    }

    private string SizeOf(AddOn item)
    {
        // Always measure the real folder in Inactive, never the Community junction:
        // the path is then stable across toggles, so the cache actually hits.
        var real = _jm is null ? item.FullPath : Path.Combine(_jm.Inactive, item.FolderName);
        try { return Scanner.ReadableSize(Directory.Exists(real) ? real : item.FullPath); }
        catch { return ""; }
    }

    private static string KindLabel(AddOnKind k) => k switch
    {
        AddOnKind.Aircraft => "AIRCRAFT",
        AddOnKind.Scenery => "SCENERY",
        AddOnKind.Livery => "LIVERIES",
        AddOnKind.Instrument => "INSTRUMENTS",
        AddOnKind.Utility => "UTILITIES",
        _ => "OTHER"
    };

    private void UpdateStatus(List<AddOn> all)
    {
        var active = all.Where(a => a.IsActive).ToList();
        var totalBytes = active.Sum(a => Scanner.SizeBytes(RealPathOf(a)));

        StatusLine.Text = $"{active.Count} of {all.Count} load  ·  {Scanner.ReadableSize(totalBytes)}";

        // A rescan result is reported once, then the line returns to plain facts.
        if (_transientNote is not null)
        {
            StatusLine.Text += "  ·  " + _transientNote;
            _transientNote = null;
        }

        // Detail belongs in the tooltip, not the body.
        var odd = all.Count(a => a.Kind is AddOnKind.Other or AddOnKind.Unknown);
        StatusLine.ToolTip = _jm is null
            ? null
            : $"Off add-ons are kept in {_jm.Inactive}"
              + (odd == 0 ? "" : $"\n{odd} add-on(s) declare an unusual type — shown under Other");

        refreshUndoLabel();
        refreshBulkButtons(all);
    }

    /// <summary>
    /// An action that would change nothing looks inert, because it is. Leaving
    /// "Enable everything" clickable when everything is already on makes the app
    /// feel broken rather than finished.
    /// </summary>
    private void refreshBulkButtons(List<AddOn> all)
    {
        var total = all.Count;
        var active = all.Count(a => a.IsActive);

        EnableAllBtn.IsEnabled = total > 0 && active < total;
        DisableAllBtn.IsEnabled = active > 0;

        bool AlreadyOnly(AddOnKind kind) =>
            active == all.Count(a => a.Kind == kind) &&
            all.Where(a => a.Kind != kind).All(a => !a.IsActive);

        AircraftOnlyBtn.IsEnabled = total > 0 && !AlreadyOnly(AddOnKind.Aircraft);
        SceneryOnlyBtn.IsEnabled = total > 0 && !AlreadyOnly(AddOnKind.Scenery);
        LiveriesOnlyBtn.IsEnabled = total > 0 && !AlreadyOnly(AddOnKind.Livery);
    }

    private void refreshUndoLabel()
    {
        UndoButton.Content = string.IsNullOrEmpty(_undoLabel) ? "Undo" : "Undo: " + _undoLabel;
        UndoButton.IsEnabled = _undoSnapshot is not null;

        // One state, one treatment: disabled is opacity everywhere in this app,
        // so Undo keeps its normal colour and never fades its text separately.
        UndoButton.Foreground = (Brush)FindResource("TextBrush");
    }

    private void SetState(AddOn item, bool on)
    {
        if (_jm is null) return;
        try
        {
            if (on) _jm.Enable(item.FolderName); else _jm.Disable(item.FolderName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not change add-on",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Render();
    }

    private void Snapshot()
    {
        if (_jm is null) return;
        _undoSnapshot = Scanner.Scan(_jm.Community, _jm.Inactive)
            .Select(a => (a.FolderName, a.IsActive)).ToList();
    }

    /// <summary>Applies one toggle, returning the failure message or null.</summary>
    private string? ApplyState(string name, bool on)
    {
        if (_jm is null) return "no folder selected";
        try
        {
            if (on) _jm.Enable(name); else _jm.Disable(name);
            return null;
        }
        catch (Exception ex) { return $"{name}: {ex.Message}"; }
    }

    /// <summary>
    /// Reports partial failure honestly. A bulk action that silently skips three
    /// of twenty add-ons is worse than one that says so.
    /// </summary>
    private static void ReportFailures(List<string> failures, int attempted)
    {
        if (failures.Count == 0) return;
        var shown = string.Join("\n", failures.Take(5));
        var more = failures.Count > 5 ? $"\n…and {failures.Count - 5} more" : "";
        MessageBox.Show(
            $"{failures.Count} of {attempted} add-ons could not be changed:\n\n{shown}{more}",
            "Some changes did not apply", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Bulk(List<AddOn> items, bool on)
    {
        if (_jm is null) return;
        Snapshot();
        var failures = new List<string>();
        foreach (var i in items)
        {
            var err = ApplyState(i.FolderName, on);
            if (err is not null) failures.Add(err);
        }
        _undoLabel = LabelFor(items, on);
        Render();
        ReportFailures(failures, items.Count);
    }

    private static string LabelFor(List<AddOn> items, bool on)
    {
        var verb = on ? "enabled" : "disabled";
        if (items.Count == 1) return $"{verb} {items[0].Title ?? items[0].FolderName}";
        return $"{verb} {items.Count} add-ons";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_jm is null || _undoSnapshot is null) return;
        var restore = _undoSnapshot;
        _undoSnapshot = null;
        _undoLabel = "";
        var failures = new List<string>();
        foreach (var (name, wasActive) in restore)
        {
            var err = ApplyState(name, wasActive);
            if (err is not null) failures.Add(err);
        }
        Render();
        ReportFailures(failures, restore.Count);
    }

    private void EnableAll_Click(object s, RoutedEventArgs e) => Bulk(All(), true);
    private void DisableAll_Click(object s, RoutedEventArgs e) => Bulk(All(), false);
    private void AircraftOnly_Click(object s, RoutedEventArgs e) => Only(AddOnKind.Aircraft);
    private void SceneryOnly_Click(object s, RoutedEventArgs e) => Only(AddOnKind.Scenery);
    private void LiveriesOnly_Click(object s, RoutedEventArgs e) => Only(AddOnKind.Livery);

    private List<AddOn> All() => _jm is null ? new() : Scanner.Scan(_jm.Community, _jm.Inactive);

    private void Only(AddOnKind keep)
    {
        if (_jm is null) return;
        Snapshot();
        var all = All();
        var failures = new List<string>();
        foreach (var a in all)
        {
            var err = ApplyState(a.FolderName, a.Kind == keep);
            if (err is not null) failures.Add(err);
        }
        _undoLabel = keep switch
        {
            AddOnKind.Aircraft => "switched to aircraft only",
            AddOnKind.Scenery => "switched to scenery only",
            AddOnKind.Livery => "switched to liveries only",
            _ => ""
        };
        Render();
        ReportFailures(failures, all.Count);
    }

    // ---- search, sort, keyboard ----------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        SearchHint.Visibility = _filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            SearchBox.Text = "";
            _filter = "";
            Render();
            Groups.Focus();
            e.Handled = true;
        }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _sort = tag switch
        {
            "Size" => SortMode.Size,
            "Recent" => SortMode.Recent,
            "Status" => SortMode.Status,
            _ => SortMode.Name
        };
        paintSortButtons();
        Render();
    }

    private void paintSortButtons()
    {
        // One selection mechanism for both themes: an accent tint behind the chosen
        // segment. Using HoverBrush looked raised in dark and pressed in light, so
        // the same state read two different ways.
        var selected = (Brush)FindResource("SegmentSelectedBrush");
        var clear = Brushes.Transparent;
        var onText = (Brush)FindResource("TextBrush");
        var offText = (Brush)FindResource("MutedBrush");

        void Paint(Button b, bool active)
        {
            b.Background = active ? selected : clear;
            b.Foreground = active ? onText : offText;
        }

        Paint(SortName, _sort == SortMode.Name);
        Paint(SortSize, _sort == SortMode.Size);
        Paint(SortRecent, _sort == SortMode.Recent);
        Paint(SortStatus, _sort == SortMode.Status);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+F, or / when not already typing, jumps to search.
        if (e.Key == System.Windows.Input.Key.F5) { Rescan(); e.Handled = true; return; }

        var typing = SearchBox.IsKeyboardFocusWithin;
        if ((e.Key == System.Windows.Input.Key.F &&
             (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            || (e.Key == System.Windows.Input.Key.OemQuestion && !typing))
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void UpdateSimPanel()
    {
        var running = SimState.IsRunning();
        SimPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        SimDot.Foreground = (Brush)FindResource(running ? "AccentBrush" : "DimBrush");
        SimWord.Foreground = (Brush)FindResource(running ? "TextBrush" : "MutedBrush");
        SimWord.Text = running ? "SIM RUNNING" : "SIM CLOSED";
        SimPill.ToolTip = running
            ? "Changes take effect on your next launch"
            : "Nothing is holding the Community folder";
    }

    private void UpdateFolderPill(bool automatic)
    {
        FolderDot.Foreground = (Brush)FindResource(automatic ? "GoodBrush" : "AccentBrush");
        FolderWord.Foreground = (Brush)FindResource(automatic ? "TextBrush" : "MutedBrush");
        FolderWord.Text = automatic ? "FOLDER FOUND" : "FOLDER BY HAND";
    }

    // ---- custom window chrome -------------------------------------------

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximise(); return; }
        try { DragMove(); } catch { /* not a drag gesture; ignore */ }
    }

    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Minimise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) => ToggleMaximise();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        // WPF-native picker via Win32 interop — never WinForms.
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose your Community folder",
            Multiselect = false
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            Bind(dlg.FolderName, null);
    }

    private void DismissExplain_Click(object sender, RoutedEventArgs e)
    {
        ExplainPanel.Visibility = Visibility.Collapsed;
        Theme.ExplainDismissed = true;
        Theme.Save();
    }

    private void ShowNoFolder()
    {
        Groups.Children.Clear();
        PathLine.Text = "Could not find your Community folder automatically.";
        ExplainPanel.Visibility = Visibility.Collapsed;
        Groups.Children.Add(new Border
        {
            Style = (Style)FindResource("Card"),
            Child = new TextBlock
            {
                Text = "Click Change above and choose the Community folder yourself — it sits inside your sim's Packages folder.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)FindResource("Body")
            }
        });
    }
}
