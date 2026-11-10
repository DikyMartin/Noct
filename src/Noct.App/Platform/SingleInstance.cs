namespace Noct.App.Platform;

/// <summary>
/// Ensures one Noct process owns the audio device and the library. A second launch asks the first
/// to show its window and exits, instead of opening a competing player.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Noct.SingleInstance.v1";
    private const string ActivateEventName = @"Local\Noct.Activate.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, bool isFirst)
    {
        _mutex = mutex;
        _activate = activate;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when another launch asked this one to activate.</summary>
    public event EventHandler? ActivationRequested;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstance(mutex, activate, created);
    }

    /// <summary>Begins waiting for activation requests. Blocks on a kernel wait, so it costs no CPU.</summary>
    public void StartListening()
    {
        if (!IsFirstInstance || _listener is not null)
        {
            return;
        }

        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _activate, _stop.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
        })
        {
            IsBackground = true,
            Name = "Noct.SingleInstance",
        };
        _listener.Start();
    }

    /// <summary>Tells the running instance to come forward.</summary>
    public void SignalFirstInstance() => _activate.Set();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(1));

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a different thread than the owner; the OS frees it on exit anyway.
            }
        }

        _mutex.Dispose();
        _activate.Dispose();
        _stop.Dispose();
    }
}
