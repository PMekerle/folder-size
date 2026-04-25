using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FolderSize.Services;

public static class IconService
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // SHIL_* image-list sizes for SHGetImageList (preferred path for high-DPI icons).
    private const int SHIL_LARGE      = 0x0;   // 32x32 (system metric)
    private const int SHIL_EXTRALARGE = 0x2;   // 48x48
    private const int SHIL_JUMBO      = 0x4;   // 256x256

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);

    private static readonly Guid IID_IImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly ConcurrentDictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, BitmapSource?> _largeCache = new(StringComparer.OrdinalIgnoreCase);

    // Large/jumbo icons via the shell image list. Resolution is 256x256 (SHIL_JUMBO),
    // perfect for the home-screen drive tiles.
    public static BitmapSource? GetLargeIcon(string path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string key = (isFolder ? "D:" : "F:") + path.TrimEnd('\\').ToLowerInvariant();
        if (_largeCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            uint attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_SYSICONINDEX;
            bool isDriveRoot = isFolder && path.Length <= 3 && path.EndsWith(":\\");
            if (isFolder && !isDriveRoot)
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }
            else
            {
                bool exists = File.Exists(path) || Directory.Exists(path);
                if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var shfi = new SHFILEINFO();
            var idx = SHGetFileInfoW(path, attrs, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
            if (idx == IntPtr.Zero) { _largeCache[key] = null; return null; }

            var iid = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out var himl) != 0 || himl == IntPtr.Zero)
            { _largeCache[key] = null; return null; }

            var hIcon = ImageList_GetIcon(himl, shfi.iIcon, 0);
            if (hIcon == IntPtr.Zero) { _largeCache[key] = null; return null; }

            BitmapSource? bmp = null;
            try
            {
                bmp = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
            _largeCache[key] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to get large icon for '{path}': {ex.Message}");
            _largeCache[key] = null;
            return null;
        }
    }

    public static BitmapSource? GetIcon(string path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string key = (isFolder ? "D:" : "F:") + path.TrimEnd('\\').ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            uint attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;

            // Drive roots (C:\, D:\) should use the real Windows icon (drive-type specific).
            // For ordinary folders use the generic folder icon without touching disk — this
            // avoids hundreds of blocking I/O calls when expanding a folder with many children.
            bool isDriveRoot = isFolder && path.Length <= 3 && path.EndsWith(":\\");
            if (isFolder && !isDriveRoot)
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }
            else
            {
                bool exists = File.Exists(path) || Directory.Exists(path);
                if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var shfi = new SHFILEINFO();
            var result = SHGetFileInfoW(path, attrs, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            {
                _cache[key] = null;
                return null;
            }

            BitmapSource? bmp = null;
            try
            {
                bmp = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }

            _cache[key] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to get icon for '{path}': {ex.Message}");
            _cache[key] = null;
            return null;
        }
    }
}
