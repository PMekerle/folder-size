using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FolderSize.Models;
using FolderSize.Scanner;
using FolderSize.Services;

namespace FolderSize.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IScanner _scanner = new Win32Scanner();
    private readonly ScanDatabase _db = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Stack<ExplorerNode> _backStack = new();
    private readonly Stack<ExplorerNode> _forwardStack = new();
    private bool _navigating;

    // Multi-scan coordination: one active job per drive + a FIFO queue per drive.
    // Drive root key is lowercase, trailing-slash stripped (e.g. "c:").
    private readonly Dictionary<string, ScanJob> _activeByDrive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<ScanJob>> _queueByDrive = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ScanJob> ActiveScans { get; } = new();

    private string _pathInput = "";
    private string _status = "Double-click a folder on the left to scan it, or type a path and press Scan.";
    private Metric _currentMetric = Metric.Size;
    private ExplorerNode? _selectedNode;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _pathDebounce;
    private readonly HashSet<string> _loadingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _showFiles;
    private bool _isLoading;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }
    }

    public bool IsBusy => IsScanning || _isLoading;
    public bool ShowEmptyHint => !HasBarRows && !_isLoading && !IsScanning && (_selectedNode?.HasScanData != true);
    public bool ShowEmptyFolderHint => !HasBarRows && !_isLoading && !IsScanning && _selectedNode?.HasScanData == true;
    public string EmptyFolderText
    {
        get
        {
            var s = _selectedNode?.ScanData;
            if (s == null) return "";
            return $"{_selectedNode!.Name} is empty  •  0 files  •  0 B";
        }
    }

    public string LastUpdateText
    {
        get
        {
            var ts = _selectedNode?.ScanTimestamp;
            if (ts == null) return "";
            return $"Last scan: {FormatRelativeTime(ts.Value)}  ({ts.Value:yyyy-MM-dd HH:mm})";
        }
    }

    public bool HasLastUpdate => _selectedNode?.ScanTimestamp != null;

    public bool IsSelectedScanning
    {
        get
        {
            var sel = _selectedNode?.FullPath;
            if (string.IsNullOrEmpty(sel)) return false;
            foreach (var job in ActiveScans)
            {
                if (!job.IsRunning) continue;
                if (ScanScheduler.IsSame(job.FullPath, sel)) return true;
                if (ScanScheduler.IsAncestor(job.FullPath, sel)) return true;
                if (ScanScheduler.IsAncestor(sel, job.FullPath)) return true;
            }
            return false;
        }
    }

    private static string FormatRelativeTime(DateTime ts)
    {
        var diff = DateTime.Now - ts;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hour{((int)diff.TotalHours == 1 ? "" : "s")} ago";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} day{((int)diff.TotalDays == 1 ? "" : "s")} ago";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} month{((int)(diff.TotalDays / 30) == 1 ? "" : "s")} ago";
        return $"{(int)(diff.TotalDays / 365)} year{((int)(diff.TotalDays / 365) == 1 ? "" : "s")} ago";
    }

    public bool ShowFiles
    {
        get => _showFiles;
        set
        {
            if (SetField(ref _showFiles, value))
            {
                RefreshBarRows();
                _settings.ShowFiles = value;
                _settings.Save();
            }
        }
    }

    public bool AutoExpandTree
    {
        get => _settings.AutoExpandTree;
        set
        {
            if (_settings.AutoExpandTree != value)
            {
                _settings.AutoExpandTree = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool HideCloseSizeOnDisk
    {
        get => _settings.HideCloseSizeOnDisk;
        set
        {
            if (_settings.HideCloseSizeOnDisk != value)
            {
                _settings.HideCloseSizeOnDisk = value;
                _settings.Save();
                OnPropertyChanged();
                RefreshBarRows();
            }
        }
    }

    public string Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme != value)
            {
                _settings.Theme = value;
                _settings.Save();
                ThemeService.Apply(value);
                OnPropertyChanged();
            }
        }
    }

    public string PathInput
    {
        get => _pathInput;
        set
        {
            if (SetField(ref _pathInput, value))
            {
                OnPropertyChanged(nameof(ScanButtonLabel));
                OnPropertyChanged(nameof(IsScanLabel));
                OnPropertyChanged(nameof(IsRescanLabel));
                _pathDebounce?.Stop();
                _pathDebounce?.Start();
            }
        }
    }

    public string ScanButtonLabel
    {
        get
        {
            var p = _pathInput?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(p)) return "Scan";
            if (_db.HasScan(p)) return "Rescan";
            foreach (var saved in _db.SavedPaths)
            {
                if (IsAncestorPath(saved, p)) return "Rescan";
            }
            return "Scan";
        }
    }

    public bool IsRescanLabel => ScanButtonLabel == "Rescan";
    public bool IsScanLabel => !IsRescanLabel;

    private static bool IsAncestorPath(string ancestor, string descendant)
    {
        if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(descendant)) return false;
        var a = ancestor.TrimEnd('\\', '/').ToLowerInvariant();
        var d = descendant.TrimEnd('\\', '/').ToLowerInvariant();
        if (a == d) return false;
        return d.StartsWith(a + "\\", StringComparison.Ordinal) ||
               d.StartsWith(a + "/", StringComparison.Ordinal);
    }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public bool IsScanning => ActiveScans.Any(j => j.IsRunning);
    public bool CanScan => true;

    public string ElapsedText
    {
        get
        {
            var job = ActiveScans.FirstOrDefault(j => j.IsRunning);
            return job?.ElapsedText ?? "";
        }
    }

    public bool HasElapsed => ActiveScans.Any(j => j.IsRunning);

    // "Primary" job = most recently started running job (used by the status bar).
    private ScanJob? PrimaryJob => ActiveScans.LastOrDefault(j => j.IsRunning);

    public bool ShowActiveScansPanel => ActiveScans.Count > 0;

    public ObservableCollection<ExplorerNode> ExplorerRoots { get; } = new();
    public ObservableCollection<BarRowViewModel> BarRows { get; } = new();
    public bool HasBarRows => BarRows.Count > 0;
    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;
    public bool CanGoUp => GetParentPath(_selectedNode) != null;

    public ExplorerNode? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetField(ref _selectedNode, value))
            {
                RefreshBarRows();
                OnPropertyChanged(nameof(CanGoUp));
                OnPropertyChanged(nameof(ShowEmptyHint));
                OnPropertyChanged(nameof(ShowEmptyFolderHint));
                OnPropertyChanged(nameof(EmptyFolderText));
                OnPropertyChanged(nameof(LastUpdateText));
                OnPropertyChanged(nameof(HasLastUpdate));
                OnPropertyChanged(nameof(CanRescanSelected));
                OnPropertyChanged(nameof(CanScanNow));
                OnPropertyChanged(nameof(IsSelectedScanning));
            }
        }
    }

    public bool CanRescanSelected => _selectedNode != null && _selectedNode.IsFolder && !_selectedNode.IsReparse && !string.IsNullOrWhiteSpace(_selectedNode.FullPath);

    public async Task RescanSelectedAsync()
    {
        var n = _selectedNode;
        if (n == null || !CanRescanSelected) return;
        await ScanNodeAsync(n);
    }

    public Metric CurrentMetric
    {
        get => _currentMetric;
        set
        {
            if (SetField(ref _currentMetric, value))
            {
                OnPropertyChanged(nameof(IsMetricSize));
                OnPropertyChanged(nameof(IsMetricSizeOnDisk));
                OnPropertyChanged(nameof(IsMetricFileCount));
                foreach (var r in ExplorerRoots)
                {
                    r.RefreshMetric();
                }
                RefreshBarRows();
                _settings.CurrentMetric = value;
                _settings.Save();
                Log.Info($"Metric switched to {value} (no rescan, using cached data)");
            }
        }
    }

    public bool IsMetricSize { get => _currentMetric == Metric.Size; set { if (value) CurrentMetric = Metric.Size; } }
    public bool IsMetricSizeOnDisk { get => _currentMetric == Metric.SizeOnDisk; set { if (value) CurrentMetric = Metric.SizeOnDisk; } }
    public bool IsMetricFileCount { get => _currentMetric == Metric.FileCount; set { if (value) CurrentMetric = Metric.FileCount; } }

    public ScanDatabase Database => _db;

    public event Action? ScanCompleted;

    public void RefreshDbCachedPaths()
    {
        OnPropertyChanged(nameof(ScanButtonLabel));
        OnPropertyChanged(nameof(IsScanLabel));
        OnPropertyChanged(nameof(IsRescanLabel));
    }

    public MainViewModel()
    {
        _showFiles = _settings.ShowFiles;
        _currentMetric = _settings.CurrentMetric;

        PopulateDrives();

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) => UpdateElapsed();

        _pathDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _pathDebounce.Tick += (_, _) =>
        {
            _pathDebounce.Stop();
            OnPathDebounced();
        };
    }

    private void PopulateDrives()
    {
        try
        {
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive")
                        ?? Environment.GetEnvironmentVariable("OneDriveConsumer")
                        ?? Environment.GetEnvironmentVariable("OneDriveCommercial");
            if (!string.IsNullOrWhiteSpace(oneDrive) && Directory.Exists(oneDrive))
            {
                ExplorerRoots.Add(new ExplorerNode("OneDrive", oneDrive, NodeKind.Folder, this));
            }

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType != DriveType.Fixed &&
                    drive.DriveType != DriveType.Removable &&
                    drive.DriveType != DriveType.Network) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name.TrimEnd('\\')
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                ExplorerRoots.Add(new ExplorerNode(label, drive.RootDirectory.FullName, NodeKind.Drive, this));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to list drives: {ex.Message}");
        }
    }

    private async void OnPathDebounced()
    {
        var p = _pathInput?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(p)) return;
        if (!Directory.Exists(p)) return;
        if (_selectedNode != null && string.Equals(_selectedNode.FullPath, p, StringComparison.OrdinalIgnoreCase)) return;
        await StartScanAsync(p, autoScan: false);
    }

    private void UpdateElapsed()
    {
        foreach (var job in ActiveScans) job.NotifyElapsed();
        OnPropertyChanged(nameof(ElapsedText));
    }

    public void RequestCancel()
    {
        // Cancel all running jobs + drop queued ones.
        foreach (var job in ActiveScans.ToList())
        {
            try { job.Cts.Cancel(); } catch { }
        }
        foreach (var q in _queueByDrive.Values)
        {
            while (q.Count > 0)
            {
                var j = q.Dequeue();
                try { j.Cts.Cancel(); } catch { }
            }
        }
    }

    public async Task StartScanAsync(string? initialPath = null, bool forceRescan = false, bool autoScan = true)
    {
        var path = initialPath ?? _pathInput;
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "Enter a folder path.";
            return;
        }
        path = path.Trim('"', ' ');
        if (!Directory.Exists(path))
        {
            Status = $"Path not found: {path}";
            Log.Warn($"Scan aborted, path not found: {path}");
            return;
        }

        PathInput = path;
        var node = ResolveOrCreatePath(path);
        if (node == null) return;

        bool inDb = _db.HasScan(path);
        var ancestor = inDb ? null : FindScannedAncestor(path);
        Log.Info($"StartScanAsync path='{path}' inDb={inDb} ancestor='{ancestor}' hasScanData={node.HasScanData} forceRescan={forceRescan}");

        if (!forceRescan && node.HasScanData)
        {
            node.IsExpanded = true;
            node.IsSelected = true;
            return;
        }

        if (!forceRescan && inDb)
        {
            await TryLoadFromDbAsync(node);
            node.IsExpanded = true;
            node.IsSelected = true;
            return;
        }

        if (!forceRescan && ancestor != null)
        {
            var ancestorNode = ResolveOrCreatePath(ancestor);
            if (ancestorNode != null)
            {
                if (!ancestorNode.HasScanData)
                {
                    await TryLoadFromDbAsync(ancestorNode);
                }
                var target = ResolveOrCreatePath(path);
                if (target != null && target.HasScanData)
                {
                    target.IsExpanded = true;
                    target.IsSelected = true;
                    Log.Info($"Using ancestor scan data for {path} (ancestor={ancestor})");
                    return;
                }
                Log.Warn($"Ancestor load did not yield data for {path}; falling through to fresh scan.");
            }
        }

        if (!autoScan)
        {
            // Navigate without scanning
            node.IsExpanded = true;
            node.IsSelected = true;
            return;
        }

        await ScanNodeAsync(node, forceRescan);
    }

    // Schedule a scan. Returns after the scan has been scheduled (possibly queued),
    // NOT after it completes \u2014 subscribe to ScanCompleted for that.
    public Task ScanNodeAsync(ExplorerNode node) => ScanNodeAsync(node, forceRescan: true);

    public Task ScanNodeAsync(ExplorerNode node, bool forceRescan)
    {
        if (!node.IsFolder || node.IsReparse) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(node.FullPath)) return Task.CompletedTask;

        var job = new ScanJob(node, forceRescan);
        ScheduleJob(job);
        return Task.CompletedTask;
    }

    private void ScheduleJob(ScanJob job)
    {
        var drive = job.DriveRoot;
        _activeByDrive.TryGetValue(drive, out var active);
        if (!_queueByDrive.TryGetValue(drive, out var queue))
        {
            queue = new Queue<ScanJob>();
            _queueByDrive[drive] = queue;
        }
        var queuedPaths = queue.Select(q => q.FullPath).ToList();

        var decision = ScanScheduler.Decide(job.FullPath, job.ForceRescan, active?.FullPath, queuedPaths);
        Log.Info($"Scheduler for {job.FullPath}: {decision.Action} (active={active?.FullPath ?? "-"}, queue={queuedPaths.Count})");

        switch (decision.Action)
        {
            case SchedulerAction.AlreadyInProgress:
                Status = $"Already {(active != null && ScanScheduler.IsSame(active.FullPath, job.FullPath) ? "running" : "queued")}: {job.FullPath}";
                return;

            case SchedulerAction.RunNow:
                DropRedundantQueued(queue, decision.CancelQueuedPaths);
                StartJob(job);
                return;

            case SchedulerAction.Queue:
                queue.Enqueue(job);
                job.State = ScanJobState.Pending;
                ActiveScans.Add(job); // visible as "Queued"
                OnPropertyChanged(nameof(ShowActiveScansPanel));
                Status = $"Queued: {job.FullPath}";
                return;

            case SchedulerAction.CancelQueuedAndQueue:
                DropRedundantQueued(queue, decision.CancelQueuedPaths);
                if (active == null) { StartJob(job); }
                else { queue.Enqueue(job); job.State = ScanJobState.Pending; ActiveScans.Add(job); OnPropertyChanged(nameof(ShowActiveScansPanel)); }
                return;

            case SchedulerAction.CancelActiveAndQueue:
                DropRedundantQueued(queue, decision.CancelQueuedPaths);
                // Cancel the active scan; when it completes (canceled), its finally block
                // will drain the queue and start us.
                queue.Enqueue(job);
                job.State = ScanJobState.Pending;
                ActiveScans.Add(job);
                OnPropertyChanged(nameof(ShowActiveScansPanel));
                try { active?.Cts.Cancel(); } catch { }
                return;
        }
    }

    private void DropRedundantQueued(Queue<ScanJob> queue, IReadOnlyList<string> pathsToDrop)
    {
        if (pathsToDrop == null || pathsToDrop.Count == 0) return;
        var dropSet = new HashSet<string>(pathsToDrop.Select(p => p.TrimEnd('\\', '/').ToLowerInvariant()));
        var survivors = queue
            .Where(q => !dropSet.Contains(q.FullPath.TrimEnd('\\', '/').ToLowerInvariant()))
            .ToList();
        // Mark dropped jobs as canceled so their UI row disappears
        foreach (var q in queue.ToList())
        {
            if (dropSet.Contains(q.FullPath.TrimEnd('\\', '/').ToLowerInvariant()))
            {
                try { q.Cts.Cancel(); } catch { }
                q.State = ScanJobState.Canceled;
                ActiveScans.Remove(q);
            }
        }
        queue.Clear();
        foreach (var s in survivors) queue.Enqueue(s);
        OnPropertyChanged(nameof(ShowActiveScansPanel));
    }

    private void StartJob(ScanJob job)
    {
        _activeByDrive[job.DriveRoot] = job;
        if (!ActiveScans.Contains(job)) ActiveScans.Add(job);
        job.State = ScanJobState.Running;
        job.Stopwatch.Restart();
        if (!_tickTimer.IsEnabled) _tickTimer.Start();
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(HasElapsed));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(IsSelectedScanning));
        OnPropertyChanged(nameof(ShowActiveScansPanel));
        job.RunTask = RunJobAsync(job);
    }

    private async Task RunJobAsync(ScanJob job)
    {
        var rootLabel = job.FullPath;
        Status = $"Scanning {rootLabel}...";
        Log.Info($"Scan start: {rootLabel}");

        var progress = new Progress<ScanProgress>(p =>
        {
            job.FilesScanned = p.FilesScanned;
            job.BytesScanned = p.BytesScanned;
            job.CurrentPath = p.CurrentPath;
            if (job == PrimaryJob)
            {
                Status = $"Scanning {rootLabel}  \u2022  {p.FilesScanned:N0} files, {FormatBytes(p.BytesScanned)}  \u2022  {MiddleEllipsis(p.CurrentPath, 90)}";
            }
        });

        try
        {
            var root = await _scanner.ScanAsync(job.FullPath, progress, job.Cts.Token);
            job.Node.AttachScanData(root, DateTime.Now);
            if (ReferenceEquals(_selectedNode, job.Node) || _selectedNode == null)
            {
                job.Node.IsExpanded = true;
                job.Node.IsSelected = true;
            }
            job.Stopwatch.Stop();
            _db.Save(job.FullPath, root, job.Stopwatch.ElapsedMilliseconds);
            OnPropertyChanged(nameof(ScanButtonLabel));
            Status = $"Done: {job.FullPath}  \u2022  {root.FileCount:N0} files  \u2022  Size {FormatBytes(root.Size)}  \u2022  On disk {FormatBytes(root.SizeOnDisk)}";
            Log.Info($"Scan done in {job.Stopwatch.Elapsed.TotalSeconds:0.000}s: {root.FileCount} files, size={root.Size}, onDisk={root.SizeOnDisk}  ({job.FullPath})");
            job.State = ScanJobState.Completed;
            if (ReferenceEquals(_selectedNode, job.Node)) RefreshBarRows();
        }
        catch (OperationCanceledException)
        {
            job.Stopwatch.Stop();
            job.State = ScanJobState.Canceled;
            Status = $"Canceled: {job.FullPath}";
            Log.Info($"Scan canceled: {job.FullPath}");
        }
        catch (Exception ex)
        {
            job.Stopwatch.Stop();
            job.State = ScanJobState.Failed;
            Status = $"Scan failed: {ex.Message}";
            Log.Error($"Scan failed: {job.FullPath}", ex);
        }
        finally
        {
            _activeByDrive.Remove(job.DriveRoot);
            ActiveScans.Remove(job);
            if (ActiveScans.Count == 0) _tickTimer.Stop();

            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(HasElapsed));
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ShowEmptyHint));
            OnPropertyChanged(nameof(ShowActiveScansPanel));
            OnPropertyChanged(nameof(IsSelectedScanning));
            OnPropertyChanged(nameof(LastUpdateText));
            OnPropertyChanged(nameof(HasLastUpdate));
            OnPropertyChanged(nameof(CanRescanSelected));

            // Drain queue for this drive
            if (_queueByDrive.TryGetValue(job.DriveRoot, out var q) && q.Count > 0)
            {
                var next = q.Dequeue();
                StartJob(next);
            }

            ScanCompleted?.Invoke();
        }
    }

    public void OnExplorerNodeSelected(ExplorerNode node)
    {
        if (_selectedNode != null && _selectedNode != node && !_navigating)
        {
            _backStack.Push(_selectedNode);
            _forwardStack.Clear();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
        SelectedNode = node;
        if (node.IsFolder && !string.IsNullOrWhiteSpace(node.FullPath))
        {
            PathInput = node.FullPath;
        }
        if (node.IsFolder && !node.IsReparse && !node.HasScanData && !string.IsNullOrWhiteSpace(node.FullPath))
        {
            if (_db.HasScan(node.FullPath))
            {
                TryLoadFromDb(node);
            }
            else
            {
                var ancestor = FindScannedAncestor(node.FullPath);
                if (ancestor != null)
                {
                    _ = LoadFromAncestorAndSelectAsync(node.FullPath, ancestor);
                }
            }
        }
    }

    private async Task LoadFromAncestorAndSelectAsync(string targetPath, string ancestorPath)
    {
        try
        {
            var ancestorNode = ResolveOrCreatePath(ancestorPath);
            if (ancestorNode == null) return;
            if (!ancestorNode.HasScanData)
            {
                await TryLoadFromDbAsync(ancestorNode);
            }
            _navigating = true;
            try
            {
                var target = ResolveOrCreatePath(targetPath);
                if (target != null && target.HasScanData)
                {
                    target.IsExpanded = true;
                    target.IsSelected = true;
                    Log.Info($"Selected subtree from ancestor: {targetPath} via {ancestorPath}");
                }
            }
            finally
            {
                _navigating = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"LoadFromAncestorAndSelectAsync failed for {targetPath}", ex);
        }
    }

    public void NavigateToBarRow(BarRowViewModel row)
    {
        if (row.IsReparsePoint) return;
        // Aggregate "N files" row: clicking expands files for THIS folder only (session-scoped).
        if (string.IsNullOrEmpty(row.Node.FullPath) && !row.IsDirectory)
        {
            if (_selectedNode != null)
            {
                _selectedNode.FilesExpanded = true;
                RefreshBarRows();
            }
            return;
        }
        if (!row.IsDirectory) return;
        var path = row.Node.FullPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        bool autoExpand = AutoExpandTree;

        // Prefer staying inside the current selected node's branch (e.g. OneDrive shortcut).
        if (_selectedNode != null)
        {
            _selectedNode.IsExpanded = true;
            var childMatch = _selectedNode.Children.FirstOrDefault(c =>
                c != null && string.Equals(c.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (childMatch != null)
            {
                if (autoExpand) childMatch.IsExpanded = true;
                childMatch.IsSelected = true;
                return;
            }
        }

        var target = ResolveOrCreatePath(path);
        if (target == null) return;
        if (autoExpand) target.IsExpanded = true;
        target.IsSelected = true;
    }

    public async Task ScanSelectedAsync()
    {
        var n = _selectedNode;
        if (n == null || !n.IsFolder || n.IsReparse) return;
        if (string.IsNullOrWhiteSpace(n.FullPath)) return;
        await ScanNodeAsync(n);
    }

    public void OnItemRecycled(BarRowViewModel row)
    {
        if (row == null) return;
        var fullPath = row.Node.FullPath;
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        var scan = _selectedNode?.ScanData;
        if (scan == null) return;

        bool matched = false;
        for (int i = scan.Children.Count - 1; i >= 0; i--)
        {
            var c = scan.Children[i];
            if (string.Equals(c.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                scan.Size -= c.Size;
                scan.SizeOnDisk -= c.SizeOnDisk;
                scan.FileCount -= c.FileCount;
                if (!c.IsDirectory && !c.IsReparsePoint)
                {
                    scan.DirectFileSize -= c.Size;
                    scan.DirectFileSizeOnDisk -= c.SizeOnDisk;
                    scan.DirectFileCount -= 1;
                }
                scan.Children.RemoveAt(i);
                matched = true;
            }
        }

        // Files aren't stored as FolderNodes anymore — decrement aggregates from the row data.
        if (!matched && !row.IsDirectory && !row.IsReparsePoint && !row.IsAggregate)
        {
            scan.Size -= row.Size;
            scan.SizeOnDisk -= row.SizeOnDisk;
            scan.FileCount -= 1;
            scan.DirectFileSize -= row.Size;
            scan.DirectFileSizeOnDisk -= row.SizeOnDisk;
            scan.DirectFileCount -= 1;
        }

        // Also remove from the ExplorerNode tree if it's a folder
        if (_selectedNode != null)
        {
            for (int i = _selectedNode.Children.Count - 1; i >= 0; i--)
            {
                var c = _selectedNode.Children[i];
                if (c != null && string.Equals(c.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedNode.Children.RemoveAt(i);
                }
            }
        }

        RefreshBarRows();
        OnPropertyChanged(nameof(LastUpdateText));
    }

    public bool CanScanNow => _selectedNode != null && _selectedNode.IsFolder && !_selectedNode.IsReparse && !string.IsNullOrWhiteSpace(_selectedNode.FullPath);

    public void GoBack()
    {
        if (_backStack.Count == 0) return;
        var target = _backStack.Pop();
        if (_selectedNode != null) _forwardStack.Push(_selectedNode);
        NavigateTo(target);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        var target = _forwardStack.Pop();
        if (_selectedNode != null) _backStack.Push(_selectedNode);
        NavigateTo(target);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoUp()
    {
        var parent = GetParentPath(_selectedNode);
        if (string.IsNullOrWhiteSpace(parent)) return;
        var target = ResolveOrCreatePath(parent);
        if (target == null) return;
        target.IsExpanded = true;
        target.IsSelected = true;
    }

    private static void AddFilesFromDisk(string folderPath, List<FolderNode> items)
    {
        if (!Directory.Exists(folderPath)) return;
        long clusterSize = FolderSize.Scanner.NativeMethods.GetClusterSize(Path.GetPathRoot(folderPath) ?? "C:\\");
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            foreach (var file in Directory.EnumerateFiles(folderPath, "*", opts))
            {
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.Directory) != 0) continue;
                    long size = info.Length;
                    long onDisk;
                    try { onDisk = FolderSize.Scanner.NativeMethods.GetSizeOnDisk(file, size, clusterSize); }
                    catch { onDisk = size; }
                    items.Add(new FolderNode
                    {
                        Name = info.Name,
                        FullPath = file,
                        IsDirectory = false,
                        IsReparsePoint = (info.Attributes & FileAttributes.ReparsePoint) != 0,
                        Size = size,
                        SizeOnDisk = onDisk,
                        FileCount = 1,
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? GetParentPath(ExplorerNode? node)
    {
        if (node == null) return null;
        if (node.Kind == NodeKind.Root || node.Kind == NodeKind.Drive) return null;
        if (string.IsNullOrWhiteSpace(node.FullPath)) return null;
        try
        {
            var parent = Path.GetDirectoryName(node.FullPath.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(parent) ? null : parent;
        }
        catch { return null; }
    }

    private void NavigateTo(ExplorerNode target)
    {
        _navigating = true;
        try
        {
            target.IsExpanded = true;
            target.IsSelected = true;
        }
        finally
        {
            _navigating = false;
        }
    }

    private async void TryLoadFromDb(ExplorerNode node) => await TryLoadFromDbAsync(node);

    private async Task TryLoadFromDbAsync(ExplorerNode node)
    {
        if (_loadingPaths.Contains(node.FullPath)) return;
        _loadingPaths.Add(node.FullPath);
        try
        {
            IsLoading = true;
            Status = $"Loading {node.FullPath}...";
            var path = node.FullPath;
            var sw = Stopwatch.StartNew();
            var root = await Task.Run(() => _db.LoadMerged(path));
            var scanTime = _db.GetScanTime(path);
            sw.Stop();
            if (root != null)
            {
                node.AttachScanData(root, scanTime);
                Log.Info($"Loaded scan from DB in {sw.ElapsedMilliseconds}ms: {path}");
                if (SelectedNode == node)
                {
                    RefreshBarRows();
                    OnPropertyChanged(nameof(LastUpdateText));
                    OnPropertyChanged(nameof(HasLastUpdate));
                    OnPropertyChanged(nameof(CanRescanSelected));
                }
                Status = $"From cache: {path}  \u2022  {root.FileCount:N0} files  \u2022  Size {FormatBytes(root.Size)}  \u2022  On disk {FormatBytes(root.SizeOnDisk)}  \u2022  load {sw.ElapsedMilliseconds}ms";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"TryLoadFromDb failed for {node.FullPath}", ex);
            Status = $"Load failed: {ex.Message}";
        }
        finally
        {
            _loadingPaths.Remove(node.FullPath);
            IsLoading = false;
        }
    }

    private string? FindScannedAncestor(string path)
    {
        var normalized = path.TrimEnd('\\', '/').ToLowerInvariant();
        string? best = null;
        int bestLen = -1;
        foreach (var saved in _db.SavedPaths)
        {
            var s = saved.TrimEnd('\\', '/').ToLowerInvariant();
            if (s == normalized) continue;
            if (normalized.StartsWith(s + "\\", StringComparison.Ordinal) ||
                normalized.StartsWith(s + "/", StringComparison.Ordinal))
            {
                if (s.Length > bestLen)
                {
                    bestLen = s.Length;
                    best = saved;
                }
            }
        }
        return best;
    }

    public ExplorerNode? ResolveOrCreatePath(string path)
    {
        try
        {
            if (path.Length == 2 && path[1] == ':') path += "\\";
            var full = Path.GetFullPath(path);
            var parts = SplitPath(full);
            if (parts.Count == 0) return null;

            var driveRoot = parts[0];
            var driveNode = ExplorerRoots.FirstOrDefault(c =>
                c != null && string.Equals(c.FullPath.TrimEnd('\\'), driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (driveNode == null) return null;

            var current = driveNode;
            for (int i = 1; i < parts.Count; i++)
            {
                current.IsExpanded = true;
                var next = current.Children.FirstOrDefault(c => c != null && string.Equals(c.FullPath, parts[i], StringComparison.OrdinalIgnoreCase));
                if (next == null) return current;
                current = next;
            }
            return current;
        }
        catch (Exception ex)
        {
            Log.Warn($"ResolveOrCreatePath failed for '{path}': {ex.Message}");
            return null;
        }
    }

    private static List<string> SplitPath(string fullPath)
    {
        var result = new List<string>();
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return result;
        result.Add(root);
        var rel = fullPath.Substring(root.Length).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rel.Length == 0) return result;
        var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var cur = root;
        foreach (var s in segments)
        {
            cur = Path.Combine(cur, s);
            result.Add(cur);
        }
        return result;
    }

    private const int BarRowsPageSize = 100;
    private List<BarRowViewModel> _allBarRows = new();
    private int _barRowsShown;

    public int BarRowsShown => _barRowsShown;
    public int BarRowsTotal => _allBarRows.Count;
    public int BarRowsRemaining => Math.Max(0, _allBarRows.Count - _barRowsShown);
    public bool CanLoadMoreBarRows => _barRowsShown < _allBarRows.Count;
    public string LoadMoreBarRowsText
    {
        get
        {
            int remaining = BarRowsRemaining;
            if (remaining <= 0) return "";
            int next = Math.Min(BarRowsPageSize, remaining);
            return $"Show {next} more  ({remaining:N0} hidden)";
        }
    }

    public void LoadMoreBarRows()
    {
        if (!CanLoadMoreBarRows) return;
        int next = Math.Min(_barRowsShown + BarRowsPageSize, _allBarRows.Count);
        for (int i = _barRowsShown; i < next; i++)
        {
            BarRows.Add(_allBarRows[i]);
        }
        _barRowsShown = next;
        OnPropertyChanged(nameof(BarRowsShown));
        OnPropertyChanged(nameof(BarRowsRemaining));
        OnPropertyChanged(nameof(CanLoadMoreBarRows));
        OnPropertyChanged(nameof(LoadMoreBarRowsText));
    }

    private void RefreshBarRows()
    {
        BarRows.Clear();
        _allBarRows = new List<BarRowViewModel>();
        _barRowsShown = 0;

        var scan = _selectedNode?.ScanData;
        if (scan is not null)
        {
            var items = new List<FolderNode>();
            bool showFilesNow = _showFiles || _selectedNode?.FilesExpanded == true;

            foreach (var c in scan.Children)
            {
                if (c.IsReparsePoint) continue;
                if (c.IsDirectory)
                {
                    items.Add(c);
                }
                else if (showFilesNow)
                {
                    items.Add(c);
                }
            }

            if (showFilesNow)
            {
                bool hasFilesInScan = items.Any(c => !c.IsDirectory);
                if (!hasFilesInScan && _selectedNode != null && !string.IsNullOrEmpty(_selectedNode.FullPath))
                {
                    AddFilesFromDisk(_selectedNode.FullPath, items);
                }
            }
            else if (scan.DirectFileCount > 0)
            {
                var agg = new FolderNode
                {
                    Name = scan.DirectFileCount == 1 ? "1 file" : $"{scan.DirectFileCount:N0} files",
                    FullPath = "",
                    IsDirectory = false,
                    IsReparsePoint = false,
                    Size = scan.DirectFileSize,
                    SizeOnDisk = scan.DirectFileSizeOnDisk,
                    FileCount = scan.DirectFileCount,
                };
                items.Add(agg);
            }

            _allBarRows = items
                .Select(c => new BarRowViewModel(c, this))
                .OrderByDescending(r => r.CurrentMetricValue)
                .ThenBy(r => r.Name)
                .ToList();

            long max = _allBarRows.Count > 0 ? Math.Max(1, _allBarRows.Max(r => r.CurrentMetricValue)) : 1;
            foreach (var r in _allBarRows)
            {
                r.BarFraction = max > 0 ? (double)r.CurrentMetricValue / max : 0;
            }

            int initial = Math.Min(BarRowsPageSize, _allBarRows.Count);
            for (int i = 0; i < initial; i++)
            {
                BarRows.Add(_allBarRows[i]);
            }
            _barRowsShown = initial;
        }
        OnPropertyChanged(nameof(HasBarRows));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowEmptyFolderHint));
        OnPropertyChanged(nameof(EmptyFolderText));
        OnPropertyChanged(nameof(BarRowsShown));
        OnPropertyChanged(nameof(BarRowsTotal));
        OnPropertyChanged(nameof(BarRowsRemaining));
        OnPropertyChanged(nameof(CanLoadMoreBarRows));
        OnPropertyChanged(nameof(LoadMoreBarRowsText));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} {units[i]}" : $"{b:0.##} {units[i]}";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : "..." + s[^(max - 3)..];
    }

    private static string MiddleEllipsis(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        int keep = max - 3;
        if (keep <= 0) return "...";
        int left = keep / 2;
        int right = keep - left;
        return s.Substring(0, left) + "..." + s.Substring(s.Length - right);
    }
}
