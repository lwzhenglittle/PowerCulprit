namespace PowerCulprit.Desktop.ViewModels;

public sealed class WmiCallerRow
{
    public int Rank { get; init; }

    public string ProcessText { get; init; } = "";

    public string PidText { get; init; } = "--";

    public string CallCountText { get; init; } = "--";

    public string FailureCountText { get; init; } = "--";

    public string UniqueOperationCountText { get; init; } = "--";

    public string FirstSeenText { get; init; } = "--";

    public string LastSeenText { get; init; } = "--";

    public string LastOperationText { get; init; } = "";

    public string LastResultText { get; init; } = "--";
}
