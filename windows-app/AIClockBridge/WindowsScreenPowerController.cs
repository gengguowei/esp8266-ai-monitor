using Microsoft.Win32;

namespace AIClockBridge;

/// Keeps the clock backlight in sync with the interactive Windows session.
/// The desired state is also exposed through /status, so device reboot and
/// temporary LAN failures self-heal without touching persisted brightness.
sealed class WindowsScreenPowerController : IDisposable
{
    const string EnabledKey = "follow_windows_lock";
    static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    };

    readonly object _syncLock = new();
    CancellationTokenSource _syncCts = new();
    volatile bool _locked;
    volatile bool _suspended;
    int _enabled;
    bool _disposed;

    public WindowsScreenPowerController()
    {
        // Enabled by default. Launching in an interactive user session implies
        // unlocked; subsequent lock/unlock events keep the state authoritative.
        _enabled = Settings.Get(EnabledKey) == "0" ? 0 : 1;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Recover a device left transiently dark if the bridge previously died.
        QueueSync();
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set
        {
            var next = value ? 1 : 0;
            if (Interlocked.Exchange(ref _enabled, next) == next) return;
            Settings.Set(EnabledKey, value ? "1" : "0");
            QueueSync();
        }
    }

    public bool ScreenOn => !Enabled || (!_locked && !_suspended);

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _locked = true;
            QueueSync();
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _locked = false;
            QueueSync();
        }
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _suspended = true;
            QueueSync();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _suspended = false;
            QueueSync();
        }
    }

    void QueueSync()
    {
        CancellationToken token;
        lock (_syncLock)
        {
            if (_disposed) return;
            _syncCts.Cancel();
            _syncCts.Dispose();
            _syncCts = new CancellationTokenSource();
            token = _syncCts.Token;
        }
        _ = SyncWithRetry(ScreenOn, token);
    }

    static async Task SyncWithRetry(bool on, CancellationToken token)
    {
        foreach (var delay in RetryDelays)
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                await DeviceClient.SetScreenPower(on);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (DeviceException)
            {
                // Device may be reconnecting after resume; /status remains the
                // long-lived fallback after these short immediate retries end.
            }
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _syncCts.Cancel();
            _syncCts.Dispose();
        }
    }
}
