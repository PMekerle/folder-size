using System.Windows.Media.Imaging;
using FolderSize.Models;
using FolderSize.Services;

namespace FolderSize.ViewModels;

public sealed class BarRowViewModel : ObservableObject
{
    private double _barFraction;

    public BarRowViewModel(FolderNode node, MainViewModel owner)
    {
        Node = node;
        Owner = owner;
    }

    public FolderNode Node { get; }
    public MainViewModel Owner { get; }
    public string Name => Node.Name;
    public bool IsDirectory => Node.IsDirectory;
    public bool IsReparsePoint => Node.IsReparsePoint;

    public long Size => Node.Size;
    public long SizeOnDisk => Node.SizeOnDisk;
    public long FileCount => Node.FileCount;

    public long CurrentMetricValue => Node.GetMetric(Owner.CurrentMetric);

    public string MetricsSummary
    {
        get
        {
            bool showOnDisk = true;
            if (Owner.HideCloseSizeOnDisk && Size > 0)
            {
                double diff = System.Math.Abs(SizeOnDisk - Size) / (double)Size;
                if (diff < 0.01) showOnDisk = false;
            }
            string filesPart = FileCount == 1 ? "1 file" : $"{FileCount:N0} files";
            return showOnDisk
                ? $"{MainViewModel.FormatBytes(Size)} ({MainViewModel.FormatBytes(SizeOnDisk)} on disk) \u2022 {filesPart}"
                : $"{MainViewModel.FormatBytes(Size)} \u2022 {filesPart}";
        }
    }

    public BitmapSource? Icon => IconService.GetIcon(Node.FullPath, Node.IsDirectory || Node.IsReparsePoint);

    public bool IsAggregate => string.IsNullOrEmpty(Node.FullPath);

    public double BarFraction
    {
        get => _barFraction;
        set => SetField(ref _barFraction, value);
    }

    public void RefreshMetric()
    {
        OnPropertyChanged(nameof(CurrentMetricValue));
    }
}
