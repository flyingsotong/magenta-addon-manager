using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MagentaAddOnManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
