using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerCulprit.Desktop.ViewModels;

public partial class SourceStatusRow : ObservableObject
{
    public string SourceName { get; set; } = "";
    public string Status { get; set; } = "Unavailable";
    public string Details { get; set; } = "";
}
