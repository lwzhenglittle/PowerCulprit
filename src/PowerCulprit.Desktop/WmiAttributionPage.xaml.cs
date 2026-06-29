using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerCulprit.Desktop.ViewModels;

namespace PowerCulprit.Desktop;

public sealed partial class WmiAttributionPage : Page
{
    private WmiAttributionViewModel? _viewModel;
    public WmiAttributionViewModel? ViewModel => _viewModel;

    public WmiAttributionPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is WmiAttributionViewModel vm)
        {
            _viewModel = vm;
            Bindings.Update();
            _ = vm.InitializeAsync();
        }
    }
}
