namespace PowerCulprit.Core.Models;

public record BatteryCycle
{
    public long Id { get; init; }

    public long? DisplayCycleId { get; init; }

    public DateTime StartUtc { get; init; }

    public DateTime? EndUtc { get; init; }

    public DateTime LastSampleUtc { get; init; }

    public double? StartBatteryPercent { get; init; }

    public double? EndBatteryPercent { get; init; }

    public double? DischargePercent { get; init; }

    public double? StartRemainingMWh { get; init; }

    public double? EndRemainingMWh { get; init; }

    public double? DischargeWh { get; init; }

    public int SampleCount { get; init; }

    public bool StartedAtFullCharge { get; init; }

    public bool IsOpen { get; init; }

    public BatteryCycleConfidence Confidence { get; init; } = BatteryCycleConfidence.High;

    public DateTime EffectiveEndUtc => EndUtc ?? LastSampleUtc;
}
