namespace PowerCulprit.Core.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Local\PowerCulprit.SingleInstance";

    private Mutex? _mutex;
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

    public void Dispose()
    {
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
