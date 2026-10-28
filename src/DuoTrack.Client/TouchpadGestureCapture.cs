using System.Runtime.Versioning;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Input;

// This optional Windows API is still marked experimental by Microsoft. All
// access is isolated behind OS, metadata and IsSupported runtime checks.
#pragma warning disable CS8305

namespace DuoTrack.Client;

internal sealed class TouchpadGestureCapture : IDisposable
{
    private readonly INativeCapture? _native;
    public bool IsSupported => _native is not null;
    public event Action<SwipeDirection>? Gesture;
    public string Status => IsSupported
        ? "三指手势：左右切换窗口，上滑任务视图，下滑显示桌面。"
        : "本机暂不支持原生三指接管；支持转发驱动生成的快捷键。";

    public TouchpadGestureCapture()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) return;
        try
        {
            if (ApiInformation.IsTypePresent("Windows.UI.Input.TouchpadGesturesController")
                && TouchpadGesturesController.IsSupported())
                _native = new NativeCapture(direction => Gesture?.Invoke(direction));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or NotSupportedException or ArgumentException) { }
    }

    public void SetEnabled(bool enabled) => _native?.SetEnabled(enabled);
    public void Dispose() => _native?.Dispose();

    private interface INativeCapture : IDisposable
    {
        void SetEnabled(bool enabled);
    }

    [SupportedOSPlatform("windows10.0.26100.0")]
    private sealed class NativeCapture : INativeCapture
    {
        private readonly TouchpadGesturesController _controller;
        private readonly PhysicalGestureRecognizer _recognizer;
        private readonly Action<SwipeDirection> _gesture;
        private bool _enabled;
        private readonly HashSet<uint> _contacts = [];

        public NativeCapture(Action<SwipeDirection> gesture)
        {
            _gesture = gesture;
            _controller = TouchpadGesturesController.CreateForProcess();
            _controller.Enabled = false;
            _controller.SupportedGestures = TouchpadGlobalGestureKinds.ThreeFingerManipulations;
            _recognizer = new PhysicalGestureRecognizer
            {
                GestureSettings = GestureSettings.ManipulationTranslateX | GestureSettings.ManipulationTranslateY
                    | GestureSettings.ManipulationTranslateRailsX | GestureSettings.ManipulationTranslateRailsY,
            };
            _recognizer.ManipulationCompleted += Completed;
            _controller.PointerPressed += Pressed;
            _controller.PointerMoved += Moved;
            _controller.PointerReleased += Released;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = false;
            try
            {
                _controller.Enabled = enabled;
                _enabled = enabled;
            }
            finally
            {
                if (!enabled)
                {
                    _contacts.Clear();
                    _recognizer.CompleteGesture();
                }
            }
        }

        private void Pressed(TouchpadGesturesController sender, PointerEventArgs args)
        {
            if (_enabled && _contacts.Add(args.CurrentPoint.PointerId)) _recognizer.ProcessDownEvent(args.CurrentPoint);
        }
        private void Moved(TouchpadGesturesController sender, PointerEventArgs args)
        {
            if (_enabled && _contacts.Contains(args.CurrentPoint.PointerId)) _recognizer.ProcessMoveEvents(args.GetIntermediatePoints());
        }
        private void Released(TouchpadGesturesController sender, PointerEventArgs args)
        {
            if (_enabled && _contacts.Remove(args.CurrentPoint.PointerId)) _recognizer.ProcessUpEvent(args.CurrentPoint);
        }
        private void Completed(PhysicalGestureRecognizer sender, ManipulationCompletedEventArgs args)
        {
            if (_enabled && GestureShortcut.Direction(args.Cumulative.Translation.X, args.Cumulative.Translation.Y) is { } direction)
                _gesture(direction);
        }

        public void Dispose()
        {
            _enabled = false;
            _contacts.Clear();
            _controller.Enabled = false;
            _controller.PointerPressed -= Pressed;
            _controller.PointerMoved -= Moved;
            _controller.PointerReleased -= Released;
            _recognizer.ManipulationCompleted -= Completed;
        }
    }
}
