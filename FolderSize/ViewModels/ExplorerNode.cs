using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using FolderSize.Models;
using FolderSize.Services;

namespace FolderSize.ViewModels;

public sealed class ExplorerNode : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;
    private FolderNode? _scanData;

    public ExplorerNode(string name, string fullPath, NodeKind kind, MainViewModel owner)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
        _owner = owner;

        if (kind != NodeKind.File && kind != NodeKind.Reparse)
        {
            Children.Add(null!);
        }
    }

    public string Name { get; }
    public string FullPath { get; }
    public NodeKind Kind { get; }
    public bool IsFolder => Kind == NodeKind.Folder || Kind == NodeKind.Drive || Kind == NodeKind.Root;
    public bool IsDrive => Kind == NodeKind.Drive;
    public bool IsRoot => Kind == NodeKind.Root;
    public bool IsReparse => Kind == NodeKind.Reparse;
    public bool IsFolderIcon => Kind == NodeKind.Folder || Kind == NodeKind.Reparse;

    public BitmapSource? Icon
    {
        get
        {
            if (Kind == NodeKind.Root) return null;
            bool asFolder = Kind != NodeKind.File;
            return IconService.GetIcon(FullPath, asFolder);
        }
    }
    public bool HasScanData => _scanData != null;
    public string ScanActionText => _scanData != null ? "Rescan" : "Scan";

    public FolderNode? ScanData
    {
        get => _scanData;
        private set
        {
            if (_scanData == value) return;
            _scanData = value;
            OnPropertyChanged(nameof(HasScanData));
            OnPropertyChanged(nameof(ScanActionText));
            OnPropertyChanged(nameof(Size));
            OnPropertyChanged(nameof(SizeOnDisk));
            OnPropertyChanged(nameof(FileCount));
            OnPropertyChanged(nameof(CurrentMetricValue));
        }
    }

    public DateTime? ScanTimestamp { get; set; }
    public bool FilesExpanded { get; set; }

    public long Size => _scanData?.Size ?? 0;
    public long SizeOnDisk => _scanData?.SizeOnDisk ?? 0;
    public long FileCount => _scanData?.FileCount ?? 0;
    public long CurrentMetricValue => _scanData?.GetMetric(_owner.CurrentMetric) ?? 0;

    public ObservableCollection<ExplorerNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value)
            {
                EnsureChildrenLoaded();
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value) && value)
            {
                _owner.OnExplorerNodeSelected(this);
            }
        }
    }

    public void RefreshMetric()
    {
        OnPropertyChanged(nameof(CurrentMetricValue));
        if (_childrenLoaded)
        {
            foreach (var c in Children)
            {
                c?.RefreshMetric();
            }
        }
    }

    public void ResortChildren()
    {
        if (!_childrenLoaded) return;
        var sorted = Children.Where(c => c != null).OrderBy(Ordering).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Children.Clear();
        foreach (var c in sorted) Children.Add(c);
    }

    public void ResortChildrenRecursive()
    {
        if (!_childrenLoaded) return;
        ResortChildren();
        foreach (var c in Children)
        {
            c?.ResortChildrenRecursive();
        }
    }

    private (int rank, long neg) Ordering(ExplorerNode n)
    {
        int rank = n.Kind switch
        {
            NodeKind.Drive => 0,
            NodeKind.Folder => 1,
            NodeKind.Reparse => 1,
            NodeKind.File => 2,
            _ => 3,
        };
        return (rank, -n.CurrentMetricValue);
    }

    public void ClearScanData()
    {
        ScanData = null;
        ScanTimestamp = null;
        Children.Clear();
        _childrenLoaded = false;
        // Restore the lazy placeholder so the chevron still appears for folder-kind nodes.
        if (Kind != NodeKind.File && Kind != NodeKind.Reparse)
        {
            Children.Add(null!);
        }
    }

    public void AttachScanData(FolderNode scan, DateTime? timestamp = null)
    {
        ScanData = scan;
        ScanTimestamp = timestamp;

        // Lazy: don't materialize the entire scanned subtree up-front. Only prep this
        // node to reveal one level on expand. This avoids creating hundreds of thousands
        // of ExplorerNodes + icon lookups for a big drive scan.
        Children.Clear();
        bool hasFolderChildren = scan.Children.Any(c => c.IsDirectory && !c.IsReparsePoint);
        if (hasFolderChildren)
        {
            Children.Add(null!);        // placeholder so the expand chevron shows
            _childrenLoaded = false;
        }
        else
        {
            _childrenLoaded = true;
        }
        if (_isExpanded) EnsureChildrenLoaded();
    }

    private void EnsureChildrenLoaded()
    {
        if (_childrenLoaded) return;
        _childrenLoaded = true;
        Children.Clear();

        // Prefer scan-backed children (cached, accurate sizes). Fall back to disk enumeration.
        if (_scanData != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LoadChildrenFromScanData(_scanData);
            sw.Stop();
            Log.Info($"ExplorerNode: expanded '{FullPath}' -> {Children.Count} children from scan in {sw.ElapsedMilliseconds}ms");
            return;
        }

        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            var dirs = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(FullPath, "*", opts))
            {
                dirs.Add(d);
            }
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    var attrs = File.GetAttributes(d);
                    if ((attrs & FileAttributes.ReparsePoint) != 0
                        && FolderSize.Scanner.NativeMethods.IsJunctionOrSymlink(d))
                    {
                        continue;
                    }
                }
                catch { }
                Children.Add(new ExplorerNode(name, d, NodeKind.Folder, _owner));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot list children of {FullPath}: {ex.Message}");
        }
    }

    private void LoadChildrenFromScanData(FolderNode scan)
    {
        foreach (var child in scan.Children
            .Where(c => c.IsDirectory && !c.IsReparsePoint)
            .OrderByDescending(c => c.GetMetric(_owner.CurrentMetric))
            .ThenBy(c => c.Name))
        {
            var node = new ExplorerNode(child.Name, child.FullPath, NodeKind.Folder, _owner);
            node._scanData = child;
            node.ScanTimestamp = ScanTimestamp;
            bool hasGrand = child.Children.Any(c => c.IsDirectory && !c.IsReparsePoint);
            node.Children.Clear();
            if (hasGrand)
            {
                node.Children.Add(null!);
                node._childrenLoaded = false;
            }
            else
            {
                node._childrenLoaded = true;
            }
            Children.Add(node);
        }
    }
}

public enum NodeKind { Root, Drive, Folder, File, Reparse }
