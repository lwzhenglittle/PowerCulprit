namespace PowerCulprit.Core.Models;

/// <summary>
/// GPU engine types as reported by Windows GPU Engine performance counters.
/// </summary>
public enum GpuEngineType
{
    Other,
    ThreeD,
    Compute,
    VideoDecode,
    VideoEncode,
    Copy
}
