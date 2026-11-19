using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using MagentaAddOnManager.Core;

namespace MagentaAddOnManager;

public partial class FeedbackWindow : Window
{
    private readonly CommunityLocation? _location;
    private readonly JunctionManager? _jm;
    private readonly int _active;
    private readonly int _total;

    public FeedbackWindow(CommunityLocation? location, JunctionManager? jm, int active, int total)
    {
        InitializeComponent();
        _location = location;
        _jm = jm;
        _active = active;
        _total = total;

        EmailRun.Text = Feedback.Email;
        if (!string.IsNullOrEmpty(Feedback.GitHubRepoUrl))
            GitHubBtn.Visibility = Visibility.Visible;

        RefreshReport();
    }

    private string CurrentReport() =>
        Feedback.BuildReport(MessageBox.Text, _location, _jm, _active, _total);

    private void RefreshReport() => ReportBox.Text = CurrentReport();

    private void MessageBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RefreshReport();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CurrentReport());
            StatusLine.Text = "Copied — paste it into your email";
        }
        catch
        {
            // Clipboard can be momentarily busy; select the text so it can be copied manually.
            ReportBox.Focus();
            ReportBox.SelectAll();
            StatusLine.Text = "Select the text and press Ctrl+C";
        }
    }

    private void Email_Click(object sender, RoutedEventArgs e)
        => Feedback.OpenUrl(Feedback.MailtoUrl(CurrentReport()));

    private void GitHub_Click(object sender, RoutedEventArgs e)
        => Feedback.OpenUrl(Feedback.GitHubIssueUrl(CurrentReport()));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } catch { }
    }

    // Same compositor chrome as the main window: dark title bar, rounded corners,
    // and a maximise that respects the work area.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, 4);
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);

        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO
    { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO
    { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0024) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }
}
