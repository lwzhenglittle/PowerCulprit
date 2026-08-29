namespace PowerCulprit.Desktop.ViewModels;

/// <summary>
/// Shared historical range used by Overview, CPU attribution and WMI pages.
/// Keeping this state outside individual view models prevents navigation from
/// silently changing the range the user is investigating.
/// </summary>
public sealed class HistorySelectionState
{
    public DateTime FromUtc { get; private set; } = DateTime.MinValue;
    public DateTime ToUtc { get; private set; } = DateTime.MinValue;

    public bool HasRange => FromUtc != DateTime.MinValue && ToUtc > FromUtc;

    public event Action<DateTime, DateTime>? Changed;

    public void SetRange(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            return;

        if (FromUtc == fromUtc && ToUtc == toUtc)
            return;

        FromUtc = fromUtc;
        ToUtc = toUtc;
        Changed?.Invoke(fromUtc, toUtc);
    }

    public void Clear()
    {
        FromUtc = DateTime.MinValue;
        ToUtc = DateTime.MinValue;
    }
}
