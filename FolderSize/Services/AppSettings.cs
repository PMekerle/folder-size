using System;
using System.IO;
using System.Text.Json;
using FolderSize.Models;

namespace FolderSize.Services;

public sealed class AppSettings
{
    public bool ShowFiles { get; set; }
    public Metric CurrentMetric { get; set; } = Metric.Size;
    public bool AutoExpandTree { get; set; } = true;
    public bool HideCloseSizeOnDisk { get; set; } = true;
    public string Theme { get; set; } = "System"; // System, Light, Dark

    private static readonly string _path;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    static AppSettings()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSize");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AppSettings.Load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, _opts);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"AppSettings.Save failed: {ex.Message}");
        }
    }
}
