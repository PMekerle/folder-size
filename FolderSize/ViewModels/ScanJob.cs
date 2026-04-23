using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FolderSize.Services;

namespace FolderSize.ViewModels;

public enum ScanJobState { Pending, Running, Completed, Canceled, Failed }

public sealed class ScanJob : ObservableObject
{
    public ScanJob(ExplorerNode node, bool forceRescan)
    {
        Node = node;
        FullPath = node.FullPath ?? "";
        DriveRoot = ScanScheduler.DriveRootOf(FullPath);
        ForceRescan = forceRescan;
    }

    public ExplorerNode Node { get; }
    public string FullPath { get; }
    public string DriveRoot { get; }
    public bool ForceRescan { get; }

    public CancellationTokenSource Cts { get; } = new();
    public Stopwatch Stopwatch { get; } = new();
    public Task? RunTask { get; set; }

    private ScanJobState _state = ScanJobState.Pending;
    public ScanJobState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsPending));
            }
        }
    }

    public bool IsRunning => _state == ScanJobState.Running;
    public bool IsPending => _state == ScanJobState.Pending;

    private long _filesScanned;
    public long FilesScanned
    {
        get => _filesScanned;
        set { if (SetField(ref _filesScanned, value)) OnPropertyChanged(nameof(SummaryText)); }
    }

    private long _bytesScanned;
    public long BytesScanned
    {
        get => _bytesScanned;
        set { if (SetField(ref _bytesScanned, value)) OnPropertyChanged(nameof(SummaryText)); }
    }

    private string _currentPath = "";
    public string CurrentPath
    {
        get => _currentPath;
        set => SetField(ref _currentPath, value);
    }

    public string DisplayPath => FullPath;

    public string ElapsedText
    {
        get
        {
            int total = (int)Math.Ceiling(Stopwatch.Elapsed.TotalSeconds);
            if (total < 60) return $"{total}s";
            int m = total / 60;
            int s = total % 60;
            return $"{m}m {s:00}s";
        }
    }

    public string SummaryText
    {
        get
        {
            if (_state == ScanJobState.Pending) return "Queued";
            string size;
            double b = _bytesScanned; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            size = i == 0 ? $"{_bytesScanned:N0} {u[i]}" : $"{b:0.##} {u[i]}";
            return $"{_filesScanned:N0} files  •  {size}";
        }
    }

    public void NotifyElapsed() => OnPropertyChanged(nameof(ElapsedText));
}
