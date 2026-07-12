namespace PowerCulprit.Collectors;

/// <summary>
/// Shared status-string literals used by every collector's <c>GetStatus()</c>
/// and by <c>--diagnose</c>. Centralizing them prevents the two surfaces from
/// drifting (e.g. one writing "Unavailable" and the other "Not available").
/// Kept in Collectors (not Core) because <see cref="Core.Models.SourceStatus"/>
/// itself is value-agnostic about these strings.
/// </summary>
/// <summary>
/// Shared status-string literals used by every collector's <c>GetStatus()</c>
/// and by <c>--diagnose</c>. Centralizing them prevents the two surfaces from
/// drifting (e.g. one writing "Unavailable" and the other "Not available").
/// Kept in Collectors (not Core) because <see cref="Core.Models.SourceStatus"/>
/// itself is value-agnostic about these strings. Public so the separate Cli
/// assembly can reuse the same literals.
/// </summary>
public static class SourceStatusStrings
{
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";
    public const string Partial = "Partial";
    public const string Disabled = "Disabled";
    public const string RequiresAdmin = "Requires admin";
}
