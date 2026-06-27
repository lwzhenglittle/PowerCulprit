namespace PowerCulprit.Desktop.ViewModels;

public sealed record PowerChartSample(
    DateTime TimestampUtc,
    double? BatteryPercent,
    double? DischargeWatts);
