using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MagentaAddOnManager;

public partial class App : Application
{
    // Held for the lifetime of the process so the GC cannot release it early.
    // A second launch fails to create it and defers to the first instance.
    private Mutex? _singleInstance;

    private const string MutexName = @"Local\MagentaAddOnManager.SingleInstance.7f3a9c";
    private const string WindowTitle = "Magenta Add-On Manager";

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running: surface the existing window instead of opening a
            // second copy that would fight over the same Community folder.
            ActivateExisting();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // A UI exception must never kill the app. Log it, say so honestly, carry
        // on — the same boundary rule the bridge learned the hard way.
        DispatcherUnhandledException += OnUnhandledException;

        // Theme first, window second: applying after the window exists would show
        // a dark flash before the saved preference takes effect.
        Theme.Load();
        Theme.Apply(Theme.IsLight);

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExisting()
    {
        var hwnd = FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_RESTORE);   // un-minimise if needed
        SetForegroundWindow(hwnd);
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Magenta Add-On Manager");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {e.Exception}\n\n");
        }
        catch { /* logging must not throw inside the handler */ }

        MessageBox.Show(
            "Something went wrong, but your add-ons are untouched.\n\n" +
            e.Exception.Message + "\n\nDetails were saved to error.log in the app's settings folder.",
            "Magenta Add-On Manager", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;   // keep running
    }
}
