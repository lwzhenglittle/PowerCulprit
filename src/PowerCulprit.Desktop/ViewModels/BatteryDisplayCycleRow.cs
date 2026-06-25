using PowerCulprit.Core.Models;

namespace PowerCulprit.Desktop.ViewModels;

public sealed class BatteryDisplayCycleRow
{
    public BatteryDisplayCycle Cycle { get; init; } = new();

    public string Label { get; init; } = "";

    public string RangeText { get; init; } = "";

    public string DischargeText { get; init; } = "";

    public string RawCycleCountText { get; init; } = "";

    public string ConfidenceText { get; init; } = "";
}
