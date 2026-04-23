using System.Collections.Generic;

namespace FolderSize.Models;

public sealed class FolderNode
{
    public required string Name { get; set; }
    public required string FullPath { get; set; }
    public bool IsDirectory { get; init; }
    public bool IsReparsePoint { get; init; }

    public long Size { get; set; }
    public long SizeOnDisk { get; set; }
    public long FileCount { get; set; }

    public long DirectFileSize { get; set; }
    public long DirectFileSizeOnDisk { get; set; }
    public long DirectFileCount { get; set; }

    public FolderNode? Parent { get; set; }
    public List<FolderNode> Children { get; set; } = new();

    public long GetMetric(Metric m) => m switch
    {
        Metric.Size => Size,
        Metric.SizeOnDisk => SizeOnDisk,
        Metric.FileCount => FileCount,
        _ => 0,
    };
}
