using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PowerCulprit.Desktop.ViewModels;

namespace PowerCulprit.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _reallyClosing;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        RootFrame.Navigate(typeof(MainPage), viewModel);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));

        AppWindow.Closing += OnWindowClosing;
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
