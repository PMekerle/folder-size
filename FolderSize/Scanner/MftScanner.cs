using System;
using System.Threading;
using System.Threading.Tasks;
using FolderSize.Models;

namespace FolderSize.Scanner;

public sealed class MftScanner : IScanner
{
    public Task<FolderNode> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        throw new NotImplementedException(
            "MFT direct scanning is planned for a future version. " +
            "Requires: admin elevation, \\\\.\\C: volume handle, " +
            "FSCTL_GET_NTFS_VOLUME_DATA to locate MFT, raw MFT record parsing " +
            "(STANDARD_INFORMATION, FILE_NAME, DATA attributes, resident vs non-resident handling). " +
            "Benefits: 20-50x faster on large drives, free hardlink dedup via MFT record number.");
    }
}
