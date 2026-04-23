using System;
using System.Windows;
using FolderSize.Services;
using FolderSize.ViewModels;

namespace FolderSize;

public partial class SettingsWindow
{
    private readonly MainViewModel? _vm;

    public SettingsWindow() : this(null) { }

    public SettingsWindow(MainViewModel? vm)
    {
        _vm = vm;
        InitializeComponent();
        if (_vm != null)
        {
            ShowFilesBox.IsChecked = _vm.ShowFiles;
            AutoExpandBox.IsChecked = _vm.AutoExpandTree;
            HideCloseSizeBox.IsChecked = _vm.HideCloseSizeOnDisk;
            SelectThemeBox(_vm.Theme);
        }
        RefreshStatus();
    }

    private void SelectThemeBox(string theme)
    {
        foreach (var item in ThemeBox.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem cbi && string.Equals(cbi.Tag as string, theme, StringComparison.OrdinalIgnoreCase))
            {
                ThemeBox.SelectedItem = cbi;
                return;
            }
        }
        ThemeBox.SelectedIndex = 0;
    }

    private void ThemeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        if (ThemeBox.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag is string tag)
        {
            _vm.Theme = tag;
        }
    }

    private void ShowFilesBox_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.ShowFiles = ShowFilesBox.IsChecked == true;
    }

    private void AutoExpandBox_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.AutoExpandTree = AutoExpandBox.IsChecked == true;
    }

    private void HideCloseSizeBox_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.HideCloseSizeOnDisk = HideCloseSizeBox.IsChecked == true;
    }

    private void RefreshStatus()
    {
        bool registered = ContextMenuRegistrar.IsRegistered();
        StatusText.Text = registered ? "Registered" : "Not registered";
        StatusText.Foreground = registered
            ? System.Windows.Media.Brushes.ForestGreen
            : System.Windows.Media.Brushes.Gray;
        RegisterBtn.IsEnabled = !registered;
        UnregisterBtn.IsEnabled = registered;
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ContextMenuRegistrar.Register();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Register failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ContextMenuRegistrar.Unregister();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unregister failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
