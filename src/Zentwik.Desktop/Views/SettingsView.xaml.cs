using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;

namespace Zentwik.Desktop.Views;

public partial class SettingsView : UserControl
{
    private static readonly string[] DensityChoices = ["宽松", "标准", "紧凑"];

    public SettingsView()
    {
        InitializeComponent();
        DensityCombo.ItemsSource = DensityChoices;
        DensityCombo.SelectedItem = UiDensityService.CurrentDensity;
        ShowUnavailableToggle.IsChecked = UiDensityService.ShowUnavailableModules;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyCapabilityFilter();

    private void OnDensitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DensityCombo.SelectedItem is string density)
        {
            UiDensityService.Apply(density);
        }
    }

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
