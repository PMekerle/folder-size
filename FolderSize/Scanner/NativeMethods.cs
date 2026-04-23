using System;
using System.Runtime.InteropServices;

namespace FolderSize.Scanner;

internal static class NativeMethods
{
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    private static readonly IntPtr INVALID_HANDLE = new(-1);

    public static bool IsJunctionOrSymlink(string path)
    {
        try
        {
            IntPtr h = FindFirstFileW(path.TrimEnd('\\', '/'), out var data);
            if (h == INVALID_HANDLE) return false;
            try
            {
                uint tag = data.dwReserved0;
                return tag == IO_REPARSE_TAG_MOUNT_POINT || tag == IO_REPARSE_TAG_SYMLINK;
            }
            finally
            {
                FindClose(h);
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    public const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    public static extern uint GetLastError();

    public static long GetSizeOnDisk(string fullPath, long logicalSize, long clusterSize)
    {
        var low = GetCompressedFileSizeW(fullPath, out var high);
        if (low == INVALID_FILE_SIZE)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 0)
            {
                return RoundUpToCluster(logicalSize, clusterSize);
            }
        }
        long size = ((long)high << 32) | low;
        return size;
    }

    public static long RoundUpToCluster(long size, long clusterSize)
    {
        if (clusterSize <= 0) return size;
        if (size == 0) return 0;
        var remainder = size % clusterSize;
        return remainder == 0 ? size : size + (clusterSize - remainder);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    public static long GetClusterSize(string rootPath)
    {
        try
        {
            if (GetDiskFreeSpaceW(rootPath, out var spc, out var bps, out _, out _))
            {
                return (long)spc * bps;
            }
        }
        catch
        {
        }
        return 4096;
    }
}
