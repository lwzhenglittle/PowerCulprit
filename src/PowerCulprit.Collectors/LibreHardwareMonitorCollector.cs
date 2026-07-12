using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Collects hardware sensor readings using LibreHardwareMonitorLib.
/// Reads CPU, GPU, Memory, Motherboard, and Battery sensors when available.
/// Focuses on Power, Temperature, Load, Clock, Voltage, Energy, and Current.
/// </summary>
public class LibreHardwareMonitorCollector : IDisposable
{
    private readonly ILogger<LibreHardwareMonitorCollector> _logger;
    private readonly List<SensorBinding> _sensorBindings = new();
    private readonly List<LibreHardwareMonitor.Hardware.IHardware> _hardwareUpdateTargets = new();
    private LibreHardwareMonitor.Hardware.Computer? _computer;
    private bool _initialized;
    private bool _isAvailable;
    private int _disposed;

    private DateTime _lastSensorBindingRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan SensorBindingRefreshInterval = TimeSpan.FromSeconds(30);

    public LibreHardwareMonitorCollector(ILogger<LibreHardwareMonitorCollector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initializes and opens the LHM computer. Call once before collecting.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _computer = new LibreHardwareMonitor.Hardware.Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsBatteryEnabled = true
            };

            _computer.Open();

            foreach (var hw in _computer.Hardware)
            {
                try { UpdateHardwareTree(hw); } catch { /* skip */ }
            }

            RefreshSensorBindings(DateTime.UtcNow);
            var sensorCount = _sensorBindings.Count;

            _isAvailable = sensorCount > 0;
            _logger.LogInformation("LHM initialized: {Count} sensors across {Hw} hardware items",
                sensorCount, _computer.Hardware.Count);
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _logger.LogWarning(ex, "LibreHardwareMonitor initialization failed");
        }
    }

    /// <summary>
    /// Collects a batch of HardwareSensorSamples from all available LHM sensors.
    /// </summary>
    public IReadOnlyList<HardwareSensorSample> Collect()
    {
        var samples = new List<HardwareSensorSample>();
        var now = DateTime.UtcNow;

        if (_computer is null || !_isAvailable)
            return samples;

        try
        {
            RefreshSensorBindingsIfNeeded(now);

            foreach (var hardware in _hardwareUpdateTargets)
            {
                try
                {
                    hardware.Update();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to update hardware '{Name}'", hardware.Name);
                }
            }

            foreach (var binding in _sensorBindings)
            {
                try
                {
                    if (binding.Sensor.Value is null) continue;

                    samples.Add(new HardwareSensorSample
                    {
                        TimestampUtc = now,
                        Source = "LibreHardwareMonitor",
                        DeviceName = binding.DeviceName,
                        SensorName = binding.SensorName,
                        MetricName = binding.MetricName,
                        Value = Math.Round((double)binding.Sensor.Value, 3),
                        Unit = binding.Unit
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read sensor '{Name}'", binding.SensorName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LHM collect cycle failed");
        }

        return samples;
    }

    private void RefreshSensorBindingsIfNeeded(DateTime now)
    {
        if (now - _lastSensorBindingRefreshUtc < SensorBindingRefreshInterval)
            return;

        RefreshSensorBindings(now);
    }

    private void RefreshSensorBindings(DateTime now)
    {
        _sensorBindings.Clear();
        _hardwareUpdateTargets.Clear();

        if (_computer is null)
            return;

        foreach (var hardware in _computer.Hardware)
        {
            CollectSensorBindings(hardware);
        }

        _lastSensorBindingRefreshUtc = now;
        _isAvailable = _sensorBindings.Count > 0;
    }

    private void CollectSensorBindings(LibreHardwareMonitor.Hardware.IHardware hardware)
    {
        AddHardwareUpdateTarget(hardware);

        foreach (var sensor in hardware.Sensors)
        {
            if (!IsRelevantSensor(sensor.SensorType))
                continue;

            _sensorBindings.Add(new SensorBinding
            {
                Hardware = hardware,
                Sensor = sensor,
                DeviceName = hardware.Name,
                SensorName = sensor.Name,
                MetricName = sensor.SensorType.ToString(),
                Unit = GetUnitString(sensor.SensorType)
            });
        }

        foreach (var subHw in hardware.SubHardware)
        {
            CollectSensorBindings(subHw);
        }
    }

    private void AddHardwareUpdateTarget(LibreHardwareMonitor.Hardware.IHardware hardware)
    {
        foreach (var target in _hardwareUpdateTargets)
        {
            if (ReferenceEquals(target, hardware))
                return;
        }

        _hardwareUpdateTargets.Add(hardware);
    }

    private static void UpdateHardwareTree(LibreHardwareMonitor.Hardware.IHardware hardware)
    {
        hardware.Update();

        foreach (var subHardware in hardware.SubHardware)
        {
            UpdateHardwareTree(subHardware);
        }
    }

    /// <summary>
    /// Returns the SourceStatus for LHM.
    /// </summary>
    public SourceStatus GetStatus()
    {
        var isAdmin = false;
        try
        {
            isAdmin = System.Security.Principal.WindowsIdentity.GetCurrent()
                .Owner?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)
                ?? false;
        }
        catch { /* ignore */ }

        return new SourceStatus
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = "LibreHardwareMonitor",
            IsAvailable = _isAvailable,
            Status = _isAvailable
                ? (isAdmin ? SourceStatusStrings.Available : SourceStatusStrings.Partial)
                : SourceStatusStrings.Unavailable,
            Details = _isAvailable && !isAdmin
                ? "Some sensors may require administrator privileges"
                : null,
            RequiresAdmin = !isAdmin ? null : false
        };
    }

    // ── Helpers ──────────────────────────────────

    private static bool IsRelevantSensor(LibreHardwareMonitor.Hardware.SensorType type)
    {
        return type switch
        {
            LibreHardwareMonitor.Hardware.SensorType.Power => true,
            LibreHardwareMonitor.Hardware.SensorType.Temperature => true,
            LibreHardwareMonitor.Hardware.SensorType.Load => true,
            LibreHardwareMonitor.Hardware.SensorType.Clock => true,
            LibreHardwareMonitor.Hardware.SensorType.Voltage => true,
            LibreHardwareMonitor.Hardware.SensorType.Energy => true,
            LibreHardwareMonitor.Hardware.SensorType.Current => true,
            _ => false
        };
    }

    private static string GetUnitString(LibreHardwareMonitor.Hardware.SensorType type)
    {
        return type switch
        {
            LibreHardwareMonitor.Hardware.SensorType.Power => "W",
            LibreHardwareMonitor.Hardware.SensorType.Temperature => "°C",
            LibreHardwareMonitor.Hardware.SensorType.Load => "%",
            LibreHardwareMonitor.Hardware.SensorType.Clock => "MHz",
            LibreHardwareMonitor.Hardware.SensorType.Voltage => "V",
            LibreHardwareMonitor.Hardware.SensorType.Energy => "J",
            LibreHardwareMonitor.Hardware.SensorType.Current => "A",
            _ => ""
        };
    }

    private sealed class SensorBinding
    {
        public LibreHardwareMonitor.Hardware.IHardware Hardware { get; init; } = null!;
        public LibreHardwareMonitor.Hardware.ISensor Sensor { get; init; } = null!;
        public string DeviceName { get; init; } = string.Empty;
        public string SensorName { get; init; } = string.Empty;
        public string MetricName { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
    }

    public void Dispose()
    {
        // Guard against double-dispose from DI container teardown following an
        // explicit Dispose during shutdown.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { _computer?.Close(); } catch { /* ignore */ }
        _computer = null;
    }
}
