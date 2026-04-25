using System;
using System.IO;
using System.Windows.Media.Imaging;
using FolderSize.Services;

namespace FolderSize.ViewModels;

public sealed class DriveOverview : ObservableObject
{
    public string Label { get; }
    public string Path { get; }
    public long TotalBytes { get; }
    public long FreeBytes { get; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
    public BitmapSource? Icon => IconService.GetIcon(Path, isFolder: true);
    public BitmapSource? LargeIcon => IconService.GetLargeIcon(Path, isFolder: true);

    public string CapacityText
    {
        get
        {
            if (TotalBytes <= 0) return "";
            return $"{FormatBytes(FreeBytes)} free of {FormatBytes(TotalBytes)}";
        }
    }

    public string PercentText
    {
        get
        {
            if (TotalBytes <= 0) return "";
            return $"{UsedFraction * 100:0.0}% used";
        }
    }

    public string CapacityTooltip
    {
        get
        {
            if (TotalBytes <= 0) return "";
            return $"Used space: {FormatBytes(UsedBytes)}\nFree space: {FormatBytes(FreeBytes)}\nTotal space: {FormatBytes(TotalBytes)}";
        }
    }

    public bool IsNearlyFull => UsedFraction >= 0.90;

    private bool _isScanned;
    public bool IsScanned
    {
        get => _isScanned;
        private set { if (SetField(ref _isScanned, value)) { OnPropertyChanged(nameof(IsNotScanned)); OnPropertyChanged(nameof(ScanSummaryText)); } }
    }
    public bool IsNotScanned => !_isScanned;

    private long _scannedSize, _scannedSizeOnDisk, _scannedFileCount;
    private DateTime? _scannedAt;
    public DateTime? ScannedAt { get => _scannedAt; private set { if (SetField(ref _scannedAt, value)) OnPropertyChanged(nameof(ScannedAtText)); } }

    public string ScanSummaryText
    {
        get
        {
            if (!_isScanned) return "";
            string filesPart = _scannedFileCount == 1 ? "1 file" : $"{_scannedFileCount:N0} files";
            bool showOnDisk = _scannedSize > 0 && Math.Abs(_scannedSizeOnDisk - _scannedSize) / (double)_scannedSize >= 0.01;
            return showOnDisk
                ? $"{FormatBytes(_scannedSize)} ({FormatBytes(_scannedSizeOnDisk)} on disk) • {filesPart}"
                : $"{FormatBytes(_scannedSize)} • {filesPart}";
        }
    }

    public string ScannedAtText
    {
        get
        {
            if (_scannedAt == null) return "";
            return $"Last scan: {RelativeTime(_scannedAt.Value)}";
        }
    }

    public DriveOverview(string label, string path, long total, long free)
    {
        Label = label;
        Path = path;
        TotalBytes = total;
        FreeBytes = free;
    }

    public void SetScanData(long size, long sizeOnDisk, long fileCount, DateTime scannedAt)
    {
        _scannedSize = size;
        _scannedSizeOnDisk = sizeOnDisk;
        _scannedFileCount = fileCount;
        ScannedAt = scannedAt;
        IsScanned = true;
    }

    public void ClearScanData()
    {
        _scannedSize = _scannedSizeOnDisk = _scannedFileCount = 0;
        ScannedAt = null;
        IsScanned = false;
    }

    public static DriveOverview FromDriveInfo(DriveInfo d)
    {
        var label = string.IsNullOrWhiteSpace(d.VolumeLabel)
            ? d.Name.TrimEnd('\\')
            : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
        long total = 0, free = 0;
        try { total = d.TotalSize; free = d.AvailableFreeSpace; } catch { }
        return new DriveOverview(label, d.RootDirectory.FullName, total, free);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        double b = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} {u[i]}" : $"{b:0.##} {u[i]}";
    }

    private static string RelativeTime(DateTime when)
    {
        var diff = DateTime.Now - when;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) { int m = (int)diff.TotalMinutes; return m == 1 ? "1 min ago" : $"{m} min ago"; }
        if (diff.TotalHours < 24) { int h = (int)diff.TotalHours; return h == 1 ? "1 hour ago" : $"{h} hours ago"; }
        if (diff.TotalDays < 30) { int d = (int)diff.TotalDays; return d == 1 ? "1 day ago" : $"{d} days ago"; }
        if (diff.TotalDays < 365) { int mo = (int)(diff.TotalDays / 30); return mo == 1 ? "1 month ago" : $"{mo} months ago"; }
        int y = (int)(diff.TotalDays / 365);
        return y == 1 ? "1 year ago" : $"{y} years ago";
    }
}
