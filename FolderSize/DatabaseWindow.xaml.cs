using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FolderSize.Services;
using FolderSize.ViewModels;

namespace FolderSize;

public partial class DatabaseWindow
{
    private readonly ScanDatabase _db;
    private readonly MainViewModel? _vm;
    public ObservableCollection<SummaryRow> Rows { get; } = new();

    public event Action? EntriesChanged;

    public DatabaseWindow(MainViewModel vm)
    {
        _vm = vm;
        _db = vm.Database;
        InitializeComponent();
        Grid.ItemsSource = Rows;
        Refresh();
        _vm.ScanCompleted += OnScanCompleted;
        Closed += (_, _) => _vm.ScanCompleted -= OnScanCompleted;
    }

    private void OnScanCompleted()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnScanCompleted));
            return;
        }
        Refresh();
        EntriesChanged?.Invoke();
    }

    private void Refresh()
    {
        Rows.Clear();
        foreach (var s in _db.GetAllSummaries())
        {
            Rows.Add(new SummaryRow(s));
        }
        var total = _db.GetDbFileSize();
        StatusText.Text = $"{Rows.Count} entr{(Rows.Count == 1 ? "y" : "ies")}  \u2022  total DB file size: {FormatBytes(total)}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string path)
        {
            _db.Delete(path);
            _vm?.OnScanEntryDeleted(path);
            Refresh();
            EntriesChanged?.Invoke();
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string path) return;
        if (_vm == null) return;
        await _vm.StartScanAsync(path, forceRescan: true);
        // OnScanCompleted will refresh us.
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} {units[i]}" : $"{b:0.##} {units[i]}";
    }

    public sealed class SummaryRow
    {
        public SummaryRow(ScanSummary s) { _s = s; }
        private readonly ScanSummary _s;
        public string Path => _s.Path;
        public DateTime ScannedAt => _s.ScannedAt;
        public long Size => _s.Size;
        public long SizeOnDisk => _s.SizeOnDisk;
        public long FileCount => _s.FileCount;
        public long BlobSize => _s.BlobSize;
        public long DurationMs => _s.DurationMs;
        public string SizeText => FormatBytes(Size);
        public string SizeOnDiskText => FormatBytes(SizeOnDisk);
        public string FileCountText => $"{FileCount:N0}";
        public string BlobSizeText => FormatBytes(BlobSize);
        public string DurationText
        {
            get
            {
                if (DurationMs <= 0) return "";
                double secs = DurationMs / 1000.0;
                if (secs < 60) return $"{secs:0.0}s";
                int m = (int)(secs / 60);
                int s = (int)(secs - m * 60);
                return $"{m}m {s:00}s";
            }
        }
        public string ScannedText => $"{ScannedAt:yyyy-MM-dd HH:mm} ({RelativeTime(ScannedAt)})";

        private static string RelativeTime(DateTime when)
        {
            var diff = DateTime.Now - when;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60)
            {
                int m = (int)diff.TotalMinutes;
                return m == 1 ? "1 minute ago" : $"{m} minutes ago";
            }
            if (diff.TotalHours < 24)
            {
                int h = (int)diff.TotalHours;
                return h == 1 ? "1 hour ago" : $"{h} hours ago";
            }
            if (diff.TotalDays < 30)
            {
                int d = (int)diff.TotalDays;
                return d == 1 ? "1 day ago" : $"{d} days ago";
            }
            if (diff.TotalDays < 365)
            {
                int mo = (int)(diff.TotalDays / 30);
                return mo == 1 ? "1 month ago" : $"{mo} months ago";
            }
            int y = (int)(diff.TotalDays / 365);
            return y == 1 ? "1 year ago" : $"{y} years ago";
        }
    }
}
