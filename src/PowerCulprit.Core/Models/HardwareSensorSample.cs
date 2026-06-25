namespace PowerCulprit.Core.Models;

/// <summary>
/// A single reading from a hardware sensor (LibreHardwareMonitor, Intel tools, etc.).
/// </summary>
public record HardwareSensorSample
{
    /// <summary>UTC timestamp of the sample.</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>Data source identifier (e.g., LibreHardwareMonitor, IntelPCM).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Device name (e.g., "CPU Package", "Intel Arc B390").</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Sensor name (e.g., "Package", "Core", "Graphics").</summary>
    public string SensorName { get; init; } = string.Empty;

    /// <summary>Metric name (e.g., "Power", "Temperature", "Load", "Clock").</summary>
    public string MetricName { get; init; } = string.Empty;

    /// <summary>Measured value.</summary>
    public double Value { get; init; }

    /// <summary>Unit of measurement (e.g., "W", "°C", "MHz", "%").</summary>
    public string Unit { get; init; } = string.Empty;
}
