using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PowerCulprit.Desktop.Controls;
using PowerCulprit.Desktop.ViewModels;

namespace PowerCulprit.Desktop;

public sealed partial class CpuAttributionPage : Page
{
    private CpuAttributionViewModel? _viewModel;
    public CpuAttributionViewModel? ViewModel => _viewModel;

    public CpuAttributionPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is CpuAttributionViewModel vm)
        {
            _viewModel = vm;
            Bindings.Update();
            _ = vm.InitializeAsync();
        }
    }

    private void CpuChart_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        RootScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
    }

    private void CpuChart_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        RestorePageScrolling();
    }

    private void CpuChart_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        RestorePageScrolling();
    }

    private void CpuChart_VisibleRangeChanged(object sender, VisibleRangeChangedEventArgs e)
    {
        ViewModel?.SetChartVisibleRangeFromUserInteraction(e.FromUtc, e.ToUtc);
    }

    private void RestorePageScrolling()
    {
        RootScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
    }
}
