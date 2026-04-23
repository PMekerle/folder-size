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
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

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

    public static BitmapSource? GetIcon(string path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string key = (isFolder ? "D:" : "F:") + path.TrimEnd('\\').ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            uint attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;

            bool exists = File.Exists(path) || Directory.Exists(path);
            if (!exists)
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
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
