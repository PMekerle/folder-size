using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FolderSize.Services;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        Wpf.Ui.Appearance.ApplicationTheme chosen = theme switch
        {
            "Light" => Wpf.Ui.Appearance.ApplicationTheme.Light,
            "Dark" => Wpf.Ui.Appearance.ApplicationTheme.Dark,
            _ => IsSystemDark() ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
        };

        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(chosen);
            bool isDark = chosen == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            ApplyBrushes(isDark);
            Log.Info($"Theme applied: {theme} -> {chosen}");
        }
        catch (Exception ex)
        {
            Log.Error($"Theme apply failed for {theme}", ex);
        }
    }

    private static void ApplyBrushes(bool isDark)
    {
        var app = Application.Current;
        if (app == null) return;
        SolidColorBrush track, fill, barText, treeText, treeSelBg, treeSelText;
        if (isDark)
        {
            track       = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2E, 0x31)));
            fill        = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x5A, 0x78)));
            barText     = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)));
            treeText    = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
            treeSelBg   = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x4E, 0x6B)));
            treeSelText = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        }
        else
        {
            track       = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)));
            fill        = Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF8)));
            barText     = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
            treeText    = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));
            treeSelBg   = Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF8)));
            treeSelText = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));
        }
        app.Resources["BarTrackBrush"] = track;
        app.Resources["BarFillBrush"] = fill;
        app.Resources["BarTextBrush"] = barText;

        // Override system colors used by TreeViewItem (both for default text and selection)
        app.Resources[SystemColors.HighlightBrushKey]            = treeSelBg;
        app.Resources[SystemColors.HighlightTextBrushKey]        = treeSelText;
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey]     = treeSelBg;
        app.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = treeSelText;
        app.Resources[SystemColors.ControlTextBrushKey]          = treeText;
        app.Resources[SystemColors.WindowTextBrushKey]           = treeText;
        app.Resources[SystemColors.GrayTextBrushKey]             = Freeze(new SolidColorBrush(isDark ? Color.FromRgb(0xA8, 0xA8, 0xA8) : Color.FromRgb(0x6E, 0x6E, 0x6E)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public static bool IsSystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = k?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0;
        }
        catch { }
        return false;
    }
}
