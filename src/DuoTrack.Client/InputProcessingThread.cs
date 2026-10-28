using System.Windows.Threading;

namespace DuoTrack.Client;

// Owns an STA message loop independently of WPF layout/rendering. Diagnostic
// isolation uses separate instances for capture/hooks and received input.
internal sealed class InputProcessingThread : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopping;
    public Dispatcher Dispatcher => _ready.Task.GetAwaiter().GetResult();

    public InputProcessingThread()
    {
        _thread = new Thread(() =>
        {
            _ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            System.Windows.Threading.Dispatcher.Run();
        }) { Name = "DuoTrack input", IsBackground = false };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public T Invoke<T>(Func<T> action) => Dispatcher.CheckAccess() ? action() : Dispatcher.Invoke(action);
    public void Invoke(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.Invoke(action);
    }

    public Task StopAsync(Action cleanup)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() =>
        {
            try { cleanup(); }
            finally { Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }, DispatcherPriority.Send).Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0) Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        if (!_thread.Equals(Thread.CurrentThread)) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
