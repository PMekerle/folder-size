using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace FolderSize.Services;

public static class ContextMenuRegistrar
{
    private const string KeyName = "FolderSize";
    private const string MenuLabel = "Folder Size";

    private static readonly string[] HostKeys =
    {
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\Background\shell",
        @"Software\Classes\Drive\shell",
    };

    public static string ExePath
    {
        get
        {
            // Environment.ProcessPath is the single-file-app-safe way to get the exe path.
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path;
            var process = Process.GetCurrentProcess().MainModule?.FileName;
            return process ?? "";
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\Directory\shell\{KeyName}");
            return k != null;
        }
        catch { return false; }
    }

    public static void Register()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("Could not determine exe path.");

        foreach (var host in HostKeys)
        {
            CleanupExistingEntries(host);
            using var shell = Registry.CurrentUser.CreateSubKey($@"{host}\{KeyName}");
            shell.SetValue(null, MenuLabel, RegistryValueKind.String);
            shell.SetValue("Icon", $"\"{exe}\",0", RegistryValueKind.String);

            using var cmd = shell.CreateSubKey("command");
            bool background = host.Contains("Background", StringComparison.OrdinalIgnoreCase);
            var arg = background ? "%V" : "%1";
            cmd.SetValue(null, $"\"{exe}\" \"{arg}\"", RegistryValueKind.String);
        }
        Log.Info($"ContextMenuRegistrar: registered '{MenuLabel}' pointing to {exe}");
    }

    public static void Unregister()
    {
        foreach (var host in HostKeys)
        {
            CleanupExistingEntries(host);
        }
        Log.Info("ContextMenuRegistrar: unregistered");
    }

    private static void CleanupExistingEntries(string hostPath)
    {
        try
        {
            using var shell = Registry.CurrentUser.OpenSubKey(hostPath, writable: true);
            if (shell == null) return;
            foreach (var name in shell.GetSubKeyNames())
            {
                bool match = name.Equals(KeyName, StringComparison.OrdinalIgnoreCase)
                    || name.Replace(" ", "").IndexOf("foldersize", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match)
                {
                    try
                    {
                        using var cmdKey = shell.OpenSubKey($@"{name}\command");
                        var val = cmdKey?.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(val) && val.IndexOf("FolderSize.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            match = true;
                        }
                    }
                    catch { }
                }
                if (match)
                {
                    try
                    {
                        shell.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                        Log.Info($"ContextMenuRegistrar: removed stale entry {hostPath}\\{name}");
                    }
                    catch (Exception ex) { Log.Warn($"Could not remove {hostPath}\\{name}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"CleanupExistingEntries failed for {hostPath}: {ex.Message}"); }
    }
}
