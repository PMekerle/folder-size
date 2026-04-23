using System;
using System.Threading;
using System.Threading.Tasks;
using FolderSize.Models;

namespace FolderSize.Scanner;

public interface IScanner
{
    Task<FolderNode> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct);
}
