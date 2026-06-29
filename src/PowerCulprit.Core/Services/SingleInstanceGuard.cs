namespace PowerCulprit.Core.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Local\PowerCulprit.SingleInstance";
    public const string ShowWindowEventName = @"Local\PowerCulprit.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowRegistration;
    private bool _ownsMutex;

    public bool TryAcquire()
    {
        if (_mutex is not null)
            return _ownsMutex;

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            _ownsMutex = true;
            return true;
        }

        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return _ownsMutex;
    }

    public static bool IsAnotherInstanceRunning()
    {
        using var probe = new SingleInstanceGuard();
        return !probe.TryAcquire();
    }

    public static bool SignalExistingInstance()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var showWindowEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
            return showWindowEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void StartShowWindowListener(Action showWindow)
    {
        if (!_ownsMutex || _showWindowRegistration is not null)
            return;
        if (!OperatingSystem.IsWindows())
            return;

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            static (state, timedOut) =>
            {
                if (!timedOut && state is Action callback)
                    callback();
            },
            showWindow,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _showWindowRegistration?.Unregister(null);
        _showWindowRegistration = null;

        _showWindowEvent?.Dispose();
        _showWindowEvent = null;

        if (_mutex is null)
            return;

        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* best effort */ }
        }

        _mutex.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}
