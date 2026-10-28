namespace DuoTrack.Client;

internal enum GestureCaptureState { Inactive, Recovering, Ready, Blocked, Unavailable }

// Runs only on the capture dispatcher. OS calls are supplied separately so the
// denied/asynchronous activation and focus-loss paths can be tested deterministically.
internal sealed class GestureCaptureSession(Func<bool> isForeground, Action requestForeground,
    Func<bool, bool> setEnabled)
{
    private bool _active, _enabled;
    private int _attempts;
    private long _nextAttempt;
    public GestureCaptureState State { get; private set; }
    public event Action<GestureCaptureState>? StateChanged;
    public bool CanDispatch => _active && _enabled && State == GestureCaptureState.Ready && isForeground();

    public void Start(long now)
    {
        _active = true;
        _attempts = 0;
        _nextAttempt = now;
        if (State is GestureCaptureState.Blocked or GestureCaptureState.Unavailable)
            SetState(GestureCaptureState.Recovering);
        Poll(now);
    }

    public void FocusLost(long now)
    {
        if (!_active) return;
        // Disable before completing any in-progress recognition: its completion
        // callback must never emit a shortcut during loss of capture.
        Disable();
        if (State == GestureCaptureState.Ready)
        {
            _attempts = 0;
            _nextAttempt = now;
            SetState(GestureCaptureState.Recovering);
        }
    }

    public void Poll(long now)
    {
        if (!_active) return;
        if (!isForeground())
        {
            FocusLost(now);
            if (_attempts >= 3) { SetState(GestureCaptureState.Blocked); return; }
            SetState(GestureCaptureState.Recovering);
            if (now < _nextAttempt) return;
            _attempts++;
            _nextAttempt = now + (_attempts == 3 ? 2000 : 150);
            requestForeground();
            // SetForegroundWindow can be denied or complete asynchronously.
            // Only actual foreground ownership permits a Ready state.
            if (!isForeground())
            {
                if (_attempts == 3) SetState(GestureCaptureState.Blocked);
                return;
            }
        }
        if (State == GestureCaptureState.Unavailable) return;
        if (!_enabled)
        {
            _enabled = setEnabled(true);
            if (!_enabled)
            {
                _nextAttempt = now + 2000;
                SetState(GestureCaptureState.Unavailable);
                return;
            }
        }
        _attempts = 0;
        SetState(GestureCaptureState.Ready);
    }

    public void OnLocalInput(long now)
    {
        if (!_active) return;
        if ((State is GestureCaptureState.Blocked or GestureCaptureState.Unavailable) && now >= _nextAttempt)
        {
            _attempts = 0;
            _nextAttempt = now;
            SetState(GestureCaptureState.Recovering);
        }
        Poll(now);
    }

    public void Stop()
    {
        _active = false;
        Disable();
        SetState(GestureCaptureState.Inactive);
    }

    private void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        setEnabled(false);
    }

    private void SetState(GestureCaptureState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
