using DuoTrack.Client;

internal static class GestureCaptureRegression
{
    public static void Run()
    {
        var foreground = false;
        var allowActivation = false;
        var activations = 0;
        var enableCalls = new List<bool>();
        GestureCaptureSession? session = null;
        session = new GestureCaptureSession(() => foreground,
            () => { activations++; if (allowActivation) foreground = true; },
            enabled =>
            {
                enableCalls.Add(enabled);
                if (!enabled && session!.CanDispatch) throw new Exception("Cancelled gesture can still emit");
                return true;
            });
        session.Start(0);
        if (session.State != GestureCaptureState.Recovering || session.CanDispatch || enableCalls.Count != 0)
            throw new Exception("Denied initial activation incorrectly reported capture ready");
        session.Poll(149);
        if (activations != 1) throw new Exception("Foreground retry was not throttled");
        session.Poll(150); session.Poll(300);
        for (var now = 450; now < 5000; now += 150) session.Poll(now);
        if (activations != 3 || session.State != GestureCaptureState.Blocked)
            throw new Exception("Timer continues stealing focus after exhausted retries");
        session.OnLocalInput(2299);
        if (activations != 3) throw new Exception("New input bypassed retry cooldown");
        allowActivation = true;
        session.OnLocalInput(2300);
        if (!session.CanDispatch || session.State != GestureCaptureState.Ready || activations != 4)
            throw new Exception("New input failed to recover capture");

        // Focus is lost before the timer observes it: no stale shortcut may escape.
        foreground = false;
        if (session.CanDispatch) throw new Exception("Background process can emit gesture");
        session.FocusLost(2400);
        if (enableCalls[^1] || session.CanDispatch) throw new Exception("Focus loss did not cancel recognition");
        session.Poll(2400);
        if (!session.CanDispatch || enableCalls[^1] != true) throw new Exception("Capture did not re-enable after focus recovery");

        session.Stop();
        var count = activations;
        foreground = false;
        session.Poll(10000); session.OnLocalInput(10000); session.FocusLost(10000);
        if (activations != count || session.CanDispatch || session.State != GestureCaptureState.Inactive)
            throw new Exception("Stopped session reacquired focus");

        // A foreground change can complete later than SetForegroundWindow returns.
        var asynchronous = new GestureCaptureSession(() => foreground, () => { }, _ => true);
        asynchronous.Start(0);
        foreground = true;
        asynchronous.Poll(100);
        if (!asynchronous.CanDispatch) throw new Exception("Delayed foreground activation ignored");
        asynchronous.Stop();

        var enableAttempts = 0;
        var apiWorks = false;
        var errors = new GestureCaptureSession(() => true, () => throw new Exception("Unexpected activation"),
            enabled => { if (enabled) enableAttempts++; return !enabled || apiWorks; });
        errors.Start(0);
        errors.Poll(150); errors.Poll(300);
        if (errors.State != GestureCaptureState.Unavailable || errors.CanDispatch || enableAttempts != 1)
            throw new Exception("Native API failure is hidden or repeatedly retried");
        apiWorks = true;
        errors.OnLocalInput(2000);
        if (!errors.CanDispatch || enableAttempts != 2) throw new Exception("API recovery failed");
        errors.Stop();
        apiWorks = false;
        errors.Start(3000);
        apiWorks = true;
        errors.Start(3001);
        if (!errors.CanDispatch) throw new Exception("Repeated activation did not retry unavailable capture");
        errors.Stop();
        Console.WriteLine("PASS: gesture capture denial, bounded retries, physical-input recovery, asynchronous activation, focus-loss cancellation, API failure, and stopped-session isolation");
    }
}
