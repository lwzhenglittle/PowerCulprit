using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PowerCulprit.Desktop.ViewModels;

namespace PowerCulprit.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CpuAttributionViewModel _cpuViewModel;
    private bool _reallyClosing;

    public MainWindow(MainViewModel viewModel, CpuAttributionViewModel cpuViewModel)
    {
        InitializeComponent();
        ConfigureTitleBar();
        SetWindowIcon();

        _viewModel = viewModel;
        _cpuViewModel = cpuViewModel;

        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        ContentFrame.Navigate(typeof(MainPage), viewModel);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));

        AppWindow.Closing += OnWindowClosing;
    }


    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        switch (tag)
        {
            case "cpu":
                if (ContentFrame.CurrentSourcePageType != typeof(CpuAttributionPage))
                    ContentFrame.Navigate(typeof(CpuAttributionPage), _cpuViewModel);
                break;
            default:
                if (ContentFrame.CurrentSourcePageType != typeof(MainPage))
                    ContentFrame.Navigate(typeof(MainPage), _viewModel);
                break;
        }
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_reallyClosing) return; // let it close for real
        args.Cancel = true;
        sender.Hide();
    }

    public void ShowFromTray()
    {
        AppWindow.Show(true);
    }

    public void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }
}
