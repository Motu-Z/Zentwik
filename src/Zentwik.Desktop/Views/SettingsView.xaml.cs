using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;

namespace Zentwik.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        ShowUnavailableToggle.IsChecked = UiDensityService.ShowUnavailableModules;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyCapabilityFilter();

    private void OnShowUnavailableChanged(object sender, RoutedEventArgs e)
    {
        UiDensityService.ShowUnavailableModules = ShowUnavailableToggle.IsChecked == true;
        ApplyCapabilityFilter();
    }

    private void ApplyCapabilityFilter()
    {
        if (CapabilitiesList.ItemsSource is null)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(CapabilitiesList.ItemsSource);
        if (view is null)
        {
            return;
        }

        if (UiDensityService.ShowUnavailableModules)
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = static item => item is CapabilityRow row
                && !row.State.Contains("NotSupported", StringComparison.OrdinalIgnoreCase)
                && !row.State.Contains("Disabled", StringComparison.OrdinalIgnoreCase)
                && !row.State.Contains("Unavailable", StringComparison.OrdinalIgnoreCase);
        }

        view.Refresh();
    }
}
