using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FolderSize.Services;

namespace FolderSize;

public enum InitialScanMode { UseCache, Rescan, NoScan }

public partial class App : Application
{
    public string? InitialPath { get; private set; }
    public InitialScanMode InitialMode { get; private set; } = InitialScanMode.UseCache;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        var args = new List<string>(e.Args);

        if (args.Any(IsHelpFlag))
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
            PrintHelp();
            Shutdown(0);
            return;
        }

        // --test-scheduler: run ScanScheduler unit tests and exit without UI.
        if (args.Any(a => string.Equals(a, "--test-scheduler", StringComparison.OrdinalIgnoreCase)))
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
            int exit = ScanSchedulerTests.RunAll();
            Shutdown(exit);
            return;
        }

        bool rescan = args.RemoveAll(a => MatchesAny(a, "--rescan", "-r")) > 0;
        bool noScan = args.RemoveAll(a => MatchesAny(a, "--no-scan", "-n")) > 0;
        if (rescan && noScan)
        {
            // Conflicting flags: prefer --no-scan (the safer / non-destructive option).
            rescan = false;
            Log.Warn("--rescan and --no-scan both specified; honoring --no-scan");
        }
        InitialMode = noScan ? InitialScanMode.NoScan
                    : rescan ? InitialScanMode.Rescan
                    : InitialScanMode.UseCache;

        // Apply saved theme as early as possible
        try
        {
            var settings = AppSettings.Load();
            ThemeService.Apply(settings.Theme);
        }
        catch (Exception ex) { Log.Warn($"Theme startup apply failed: {ex.Message}"); }

        if (args.Count > 0)
        {
            InitialPath = string.Join(' ', args).Trim('"', ' ');
            Log.Info($"Startup with path='{InitialPath}', mode={InitialMode}");
        }
        else
        {
            Log.Info($"Startup with no path (mode={InitialMode})");
        }

        base.OnStartup(e);

        var main = new MainWindow();
        main.Show();
    }

    private static bool MatchesAny(string a, params string[] flags) =>
        flags.Any(f => string.Equals(a, f, StringComparison.OrdinalIgnoreCase));

    private static bool IsHelpFlag(string a) => MatchesAny(a, "--help", "-h", "-?", "/?");

    private static void PrintHelp()
    {
        Console.WriteLine("Folder Size — disk space analyzer for Windows");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  FolderSize.exe                Open the app, no scan");
        Console.WriteLine("  FolderSize.exe <path>         Open at <path>; use cached scan if available, else scan");
        Console.WriteLine("  FolderSize.exe <path> -r      Force rescan (ignore cache)");
        Console.WriteLine("  FolderSize.exe <path> -n      Just navigate to <path>; do not scan");
        Console.WriteLine();
        Console.WriteLine("Flags:");
        Console.WriteLine("  --rescan, -r        Always scan freshly, even if path is cached");
        Console.WriteLine("  --no-scan, -n       Navigate to path without scanning");
        Console.WriteLine("  --test-scheduler    Run scheduler unit tests and exit");
        Console.WriteLine("  --help, -h          Show this help");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Dispatcher.UnhandledException", e.Exception);
        MessageBox.Show(e.Exception.ToString(), "Unhandled exception", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
