using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FolderSize.Services;
using FolderSize.ViewModels;
using Microsoft.Win32;

namespace FolderSize;

public partial class MainWindow
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var initial = app.InitialPath;
        if (string.IsNullOrWhiteSpace(initial)) return;

        bool force = app.InitialMode == InitialScanMode.Rescan;
        bool autoScan = app.InitialMode != InitialScanMode.NoScan;
        Log.Info($"Auto-open from CLI: '{initial}' (force={force}, autoScan={autoScan})");
        await _vm.StartScanAsync(initial, forceRescan: force, autoScan: autoScan);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        bool force = _vm.ScanButtonLabel == "Rescan";
        await _vm.StartScanAsync(forceRescan: force);
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            bool force = _vm.ScanButtonLabel == "Rescan";
            await _vm.StartScanAsync(forceRescan: force);
        }
    }

    private void Database_Click(object sender, RoutedEventArgs e)
    {
        var win = new DatabaseWindow(_vm) { Owner = this };
        win.EntriesChanged += () => _vm.RefreshDbCachedPaths();
        win.ShowDialog();
        _vm.RefreshDbCachedPaths();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _vm.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _vm.GoForward();
    private void Up_Click(object sender, RoutedEventArgs e) => _vm.GoUp();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _vm.RequestCancel();
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ButtonBase b && b.Tag is ViewModels.ScanJob job)
        {
            try { job.Cts.Cancel(); } catch { }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private async void RescanSelected_Click(object sender, RoutedEventArgs e)
    {
        await _vm.RescanSelectedAsync();
    }

    private async void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ScanSelectedAsync();
    }

    private void BarRowOpenDefault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: BarRowViewModel row })
        {
            try { Process.Start(new ProcessStartInfo { FileName = row.Node.FullPath, UseShellExecute = true }); }
            catch (Exception ex) { Log.Error($"Open default failed for {row.Node.FullPath}", ex); }
        }
    }

    private void BarRowShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: BarRowViewModel row })
        {
            try { Process.Start("explorer.exe", $"/select,\"{row.Node.FullPath}\""); }
            catch (Exception ex) { Log.Error($"Show in Explorer failed for {row.Node.FullPath}", ex); }
        }
    }

    private void BarRowExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: BarRowViewModel row })
        {
            _vm.NavigateToBarRow(row);
        }
    }

    private void LoadMoreBarRows_Click(object sender, RoutedEventArgs e)
    {
        _vm.LoadMoreBarRows();
    }

    private async void BarRowRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: BarRowViewModel row }) return;
        var path = row.Node.FullPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            var missing = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Folder Size",
                Content = "Path no longer exists.",
                CloseButtonText = "OK",
            };
            await missing.ShowDialogAsync();
            return;
        }

        var kind = row.IsDirectory ? "folder" : "file";
        var dlg = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Delete",
            Content = $"Move this {kind} to the Recycle Bin?\n\n{path}\n\nSize: {ViewModels.MainViewModel.FormatBytes(row.Size)}",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            MinWidth = 520,
        };
        var result = await dlg.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        try
        {
            if (row.IsDirectory)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            Log.Info($"Moved to Recycle Bin: {path}");
            _vm.OnItemRecycled(row);
        }
        catch (Exception ex)
        {
            Log.Error($"Recycle failed for {path}", ex);
            var err = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Folder Size",
                Content = $"Could not move to Recycle Bin:\n{ex.Message}",
                CloseButtonText = "OK",
            };
            await err.ShowDialogAsync();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var items = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (items.Any(Directory.Exists))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var items = (string[])e.Data.GetData(DataFormats.FileDrop);
        var folder = items.FirstOrDefault(Directory.Exists);
        if (folder != null)
        {
            Log.Info($"Drag-drop scan: {folder}");
            await _vm.StartScanAsync(folder);
        }
    }

    private async void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem tvi && tvi.DataContext is ExplorerNode node)
        {
            // If double-click was on the expander toggle, let WPF handle the collapse/expand
            // and don't treat it as a scan trigger.
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null && d != tvi)
            {
                if (d is ToggleButton) return;
                d = VisualTreeHelper.GetParent(d);
            }

            e.Handled = true;
            if (!node.IsFolder || node.IsReparse) return;
            if (string.IsNullOrWhiteSpace(node.FullPath)) return;
            await _vm.ScanNodeAsync(node);
        }
    }

    private void BarRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not BarRowViewModel row) return;
        e.Handled = true;
        // Double-click on a file row opens it in the default app.
        if (e.ClickCount >= 2 && !row.IsDirectory && !row.IsReparsePoint && !row.IsAggregate)
        {
            var path = row.Node.FullPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
                catch (Exception ex) { Log.Error($"Open default failed for {path}", ex); }
            }
            return;
        }
        _vm.NavigateToBarRow(row);
    }

    private void BarRowProperties_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: BarRowViewModel row }) return;
        ShowShellProperties(row.Node.FullPath);
    }

    private static void ShowShellProperties(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                lpVerb = "properties",
                lpFile = path,
                nShow = 1,
                fMask = SEE_MASK_INVOKEIDLIST,
            };
            ShellExecuteEx(ref info);
        }
        catch (Exception ex) { Log.Error($"Properties failed for {path}", ex); }
    }

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    private void CtxExplore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is ExplorerNode node)
        {
            OpenInExplorer(node.FullPath);
        }
    }

    private async void CtxScan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is ExplorerNode node)
        {
            if (!node.IsFolder || node.IsReparse) return;
            await _vm.ScanNodeAsync(node);
        }
    }

    private void CtxBarExplore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is BarRowViewModel row)
        {
            OpenInExplorer(row.Node.FullPath);
        }
    }

    private async void CtxBarScan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is BarRowViewModel row)
        {
            if (!row.IsDirectory || row.IsReparsePoint) return;
            var path = row.Node.FullPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            await _vm.StartScanAsync(path, forceRescan: true);
        }
    }

    private static void OpenInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open in Explorer: {path}", ex);
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            _vm.GoBack();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            _vm.GoForward();
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        if (alt && e.Key == Key.Left) { _vm.GoBack(); e.Handled = true; }
        else if (alt && e.Key == Key.Right) { _vm.GoForward(); e.Handled = true; }
        else if (alt && e.Key == Key.Up) { _vm.GoUp(); e.Handled = true; }
    }
}
