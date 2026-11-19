using System.Diagnostics;
using System.Linq;

namespace MagentaAddOnManager.Core;

/// <summary>
/// Whether the simulator is open. Editing the Community folder while it runs is not
/// harmful, but the change is not picked up until the next launch — and silently
/// appearing to work when you are mid-troubleshoot is the worst possible outcome.
/// So we say so.
/// </summary>
public static class SimState
{
    private static readonly string[] ProcessNames =
    {
        "FlightSimulator2024",   // MSFS 2024, Store and Steam
        "FlightSimulator",       // MSFS 2020
    };

    public static bool IsRunning(out string? name)
    {
        name = null;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (ProcessNames.Any(n =>
                        string.Equals(p.ProcessName, n, System.StringComparison.OrdinalIgnoreCase)))
                    {
                        name = p.ProcessName;
                        return true;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    public static bool IsRunning() => IsRunning(out _);
}
