using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PowerCulprit.Desktop.Controls;
using PowerCulprit.Desktop.ViewModels;

namespace PowerCulprit.Desktop;

public sealed partial class MainPage : Page
{
    private MainViewModel? _viewModel;
    public MainViewModel? ViewModel => _viewModel;

    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is MainViewModel vm)
        {
            _viewModel = vm;
            // Force x:Bind to re-evaluate
            Bindings.Update();
        }
    }

    private async void ClearHistoryButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear historical data?",
            Content = "This deletes all collected power, process, GPU, hardware sensor, analysis, and source status history. Background collection will continue after the clear.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ViewModel.ClearHistoryCommand.ExecuteAsync(null);
    }

    private void HistoryChart_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        RootScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
    }

    private void HistoryChart_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        RestorePageScrolling();
    }

    private void HistoryChart_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        RestorePageScrolling();
    }

    private void HistoryChart_VisibleRangeChanged(object sender, VisibleRangeChangedEventArgs e)
    {
        ViewModel?.SetChartVisibleRangeFromUserInteraction(e.FromUtc, e.ToUtc);
    }

    private void RestorePageScrolling()
    {
        RootScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
    }
}
