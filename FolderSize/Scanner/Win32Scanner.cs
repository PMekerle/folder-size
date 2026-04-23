using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSize.Models;
using FolderSize.Services;

namespace FolderSize.Scanner;

public sealed class Win32Scanner : IScanner
{
    private static readonly EnumerationOptions _options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public Task<FolderNode> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            Log.Info($"Scan start: {rootPath}");
            var normalized = Path.GetFullPath(rootPath);
            var rootDrive = Path.GetPathRoot(normalized) ?? "C:\\";
            long clusterSize = NativeMethods.GetClusterSize(rootDrive);
            int parallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));
            Log.Info($"Cluster size for {rootDrive}: {clusterSize}, parallelism: {parallelism}");

            var rootName = Path.GetFileName(normalized.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(rootName)) rootName = normalized;
            var root = new FolderNode
            {
                Name = rootName,
                FullPath = normalized,
                IsDirectory = true,
                IsReparsePoint = false,
            };

            long filesScanned = 0;
            long bytesScanned = 0;
            long lastReportTick = Environment.TickCount64;

            void ReportMaybe(string path)
            {
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastReportTick) >= 100)
                {
                    Interlocked.Exchange(ref lastReportTick, now);
                    progress?.Report(new ScanProgress(
                        Interlocked.Read(ref filesScanned),
                        Interlocked.Read(ref bytesScanned),
                        path));
                }
            }

            // Enumerate root's direct children once (sequential).
            // Files are tallied into root aggregates directly (no per-file FolderNode to keep memory down).
            var topEntries = EnumerateDir(root.FullPath);

            var dirChildren = new List<FolderNode>();
            foreach (var (name, fullPath, attrs, logicalSize) in topEntries)
            {
                ct.ThrowIfCancellationRequested();
                bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
                bool isDir = (attrs & FileAttributes.Directory) != 0;

                if (isReparse && isDir && NativeMethods.IsJunctionOrSymlink(fullPath))
                {
                    var reparse = new FolderNode
                    {
                        Name = name, FullPath = fullPath,
                        IsDirectory = true, IsReparsePoint = true,
                        Parent = root,
                    };
                    root.Children.Add(reparse);
                }
                else if (isDir)
                {
                    var dirChild = new FolderNode
                    {
                        Name = name, FullPath = fullPath,
                        IsDirectory = true, IsReparsePoint = false,
                        Parent = root,
                    };
                    root.Children.Add(dirChild);
                    dirChildren.Add(dirChild);
                }
                else
                {
                    long sizeOnDisk = NativeMethods.GetSizeOnDisk(fullPath, logicalSize, clusterSize);
                    root.Size += logicalSize;
                    root.SizeOnDisk += sizeOnDisk;
                    root.FileCount += 1;
                    root.DirectFileSize += logicalSize;
                    root.DirectFileSizeOnDisk += sizeOnDisk;
                    root.DirectFileCount += 1;
                    Interlocked.Add(ref filesScanned, 1);
                    Interlocked.Add(ref bytesScanned, logicalSize);
                }
            }

            // Parallel scan of top-level directory children
            try
            {
                Parallel.ForEach(
                    dirChildren,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                    child =>
                    {
                        ScanDirectoryParallel(child, clusterSize, ct, ref filesScanned, ref bytesScanned, ReportMaybe);
                    });
            }
            catch (OperationCanceledException) { throw; }
            catch (AggregateException ae) when (ae.InnerExceptions.Any(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(ct);
            }

            // Add directory contributions to root (files were already tallied inline above)
            foreach (var child in dirChildren)
            {
                root.Size += child.Size;
                root.SizeOnDisk += child.SizeOnDisk;
                root.FileCount += child.FileCount;
            }

            progress?.Report(new ScanProgress(filesScanned, bytesScanned, normalized));
            Log.Info($"Scan complete: {filesScanned} files, {bytesScanned} bytes, {root.Children.Count} top-level children");
            return root;
        }, ct);
    }

    private static List<(string name, string fullPath, FileAttributes attrs, long size)> EnumerateDir(string path)
    {
        var list = new List<(string, string, FileAttributes, long)>();
        try
        {
            var enumerable = new FileSystemEnumerable<(string name, string fullPath, FileAttributes attrs, long size)>(
                path,
                (ref FileSystemEntry e) => (e.FileName.ToString(), e.ToFullPath(), e.Attributes, e.Length),
                _options);
            foreach (var e in enumerable) list.Add(e);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot enumerate {path}: {ex.Message}");
        }
        return list;
    }

    private static void ScanDirectoryParallel(
        FolderNode dir,
        long clusterSize,
        CancellationToken ct,
        ref long filesScanned,
        ref long bytesScanned,
        Action<string> reportMaybe)
    {
        ct.ThrowIfCancellationRequested();
        var entries = EnumerateDir(dir.FullPath);

        foreach (var (name, fullPath, attrs, logicalSize) in entries)
        {
            ct.ThrowIfCancellationRequested();
            bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
            bool isDir = (attrs & FileAttributes.Directory) != 0;

            if (isReparse && isDir && NativeMethods.IsJunctionOrSymlink(fullPath))
            {
                var leaf = new FolderNode
                {
                    Name = name, FullPath = fullPath,
                    IsDirectory = true, IsReparsePoint = true,
                    Parent = dir,
                };
                dir.Children.Add(leaf);
                continue;
            }

            if (isDir)
            {
                var child = new FolderNode
                {
                    Name = name, FullPath = fullPath,
                    IsDirectory = true, IsReparsePoint = false,
                    Parent = dir,
                };
                dir.Children.Add(child);
                ScanDirectoryParallel(child, clusterSize, ct, ref filesScanned, ref bytesScanned, reportMaybe);

                dir.Size += child.Size;
                dir.SizeOnDisk += child.SizeOnDisk;
                dir.FileCount += child.FileCount;
            }
            else
            {
                // Don't materialize a FolderNode per file — scales to millions of files
                // without blowing up memory. Only directory aggregates are kept.
                long sizeOnDisk;
                try { sizeOnDisk = NativeMethods.GetSizeOnDisk(fullPath, logicalSize, clusterSize); }
                catch { sizeOnDisk = NativeMethods.RoundUpToCluster(logicalSize, clusterSize); }

                dir.Size += logicalSize;
                dir.SizeOnDisk += sizeOnDisk;
                dir.FileCount += 1;
                dir.DirectFileSize += logicalSize;
                dir.DirectFileSizeOnDisk += sizeOnDisk;
                dir.DirectFileCount += 1;

                Interlocked.Add(ref filesScanned, 1);
                Interlocked.Add(ref bytesScanned, logicalSize);
                reportMaybe(fullPath);
            }
        }
    }

}
