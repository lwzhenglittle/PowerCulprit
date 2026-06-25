using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

public record BatteryCycleBuildResult(
    IReadOnlyList<BatteryCycle> RawCycles,
    IReadOnlyList<BatteryDisplayCycle> DisplayCycles);
