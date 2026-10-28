using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DuoTrack.Client;
using DuoTrack.Protocol;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--capture-diagnostics")) { CaptureDiagnostics(); return; }
        if (args.Contains("--gesture-native")) { NativeTest(gesturesOnly: true); return; }
        if (args.Contains("--thread-isolation")) { ThreadIsolationTest(args.Contains("--isolated")); return; }
        if (args.Contains("--queue-regression")) { ScrollQueueRegression.Run(); return; }
        GestureCaptureRegression.Run();
        ScrollQueueRegression.Run();
        var peer = Guid.NewGuid();
        var queue = new InputPacketQueue();
        for (var i = 0; i < 10000; i++) queue.Enqueue(new RoutedInput(peer, WheelCapture.Stamp(new InputPacket(InputEventType.MouseWheel, 1), 1000)));
        long precisionTotal = 0;
        while (queue.TryDequeue(out var sum))
        {
            if (sum.Packet.Value1 > 120) throw new Exception("Coalescing manufactured a large wheel jump");
            precisionTotal += sum.Packet.Value1;
        }
        if (precisionTotal != 10000) throw new Exception("High-resolution wheel coalescing lost scroll distance");
        InputPacket[] ordered = [new(InputEventType.MouseWheel, 1), new(InputEventType.Key, 65),
            new(InputEventType.MouseWheel, 2), new(InputEventType.MouseWheel, -3),
            new(InputEventType.MouseWheel, -4, 1), new(InputEventType.ReleaseAll)];
        foreach (var packet in ordered) queue.Enqueue(new RoutedInput(peer, packet));
        foreach (var packet in ordered)
            if (!queue.TryDequeue(out var actual) || actual.Packet != packet) throw new Exception("Scroll ordering barrier failed");
        for (var i = 0; i < 10000; i++) queue.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 120)));
        long total = 0;
        while (queue.TryDequeue(out var part)) total += part.Packet.Value1;
        if (total != 1200000) throw new Exception("Wheel merge overflow");
        var other = Guid.NewGuid();
        queue.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 1)));
        queue.Enqueue(new RoutedInput(other, new InputPacket(InputEventType.MouseWheel, 1)));
        if (queue.Count != 2) throw new Exception("Different peers were merged");
        Console.WriteLine("PASS: precision scroll distance, direction/axis/key barriers, overflow and peer isolation");

        foreach (var direction in Enum.GetValues<SwipeDirection>())
        {
            var held = new HashSet<int>();
            var packets = GestureShortcut.Packets(direction);
            foreach (var packet in packets)
                if ((packet.Flags & NativeMethods.KeyeventfKeyUp) != 0) held.Remove(packet.Value1);
                else held.Add(packet.Value1);
            if (held.Count != 0 || packets.Count < 4) throw new Exception("Gesture shortcut left a key held");
        }
        if (GestureShortcut.Direction(-100, 0) != SwipeDirection.Left || GestureShortcut.Direction(100, 0) != SwipeDirection.Right
            || GestureShortcut.Direction(0, -100) != SwipeDirection.Up || GestureShortcut.Direction(0, 100) != SwipeDirection.Down
            || GestureShortcut.Direction(1, 1) is not null) throw new Exception("Swipe direction recognition failed");
        Console.WriteLine("PASS: four gesture shortcuts, balanced key releases and small-motion rejection");

        var dispatcher = Dispatcher.CurrentDispatcher;
        var delivered = new List<InputPacket>();
        using (var dispatch = new InputDispatchQueue(dispatcher, (_, packet) => delivered.Add(packet)))
        {
            for (var i = 0; i < 10000; i++) dispatch.Enqueue(peer, WheelCapture.Stamp(new InputPacket(InputEventType.MouseWheel, 1), 1000));
            dispatch.Enqueue(peer, new InputPacket(InputEventType.ReleaseAll));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (delivered.Count != 85 || delivered.Take(84).Sum(p => p.Value1) != 10000
                || delivered.Take(84).Any(p => p.Value1 > 120) || delivered[^1].Type != InputEventType.ReleaseAll)
                throw new Exception("UI dispatch batching or release ordering failed");
        }
        Console.WriteLine("PASS: 10,000 same-frame scroll deltas delivered in bounded units plus ordered release");

        var firstDevice = new DeviceInfo(Guid.NewGuid(), "A", true);
        var secondDevice = new DeviceInfo(Guid.NewGuid(), "B", true);
        var deviceList = new ObservableCollection<DeviceInfo> { firstDevice, secondDevice };
        var collectionChanges = 0;
        deviceList.CollectionChanged += (_, _) => collectionChanges++;
        for (var refresh = 0; refresh < 1000; refresh++)
            if (DeviceListUpdater.Synchronize(deviceList, [firstDevice, secondDevice])) throw new Exception("Unchanged device list was rebuilt");
        if (collectionChanges != 0) throw new Exception("Unchanged devices raised layout changes");
        DeviceListUpdater.Synchronize(deviceList, [firstDevice with { Online = false }, secondDevice]);
        if (collectionChanges != 1 || !ReferenceEquals(deviceList[1], secondDevice)) throw new Exception("Incremental device update disturbed unaffected item");
        Console.WriteLine("PASS: 1,000 unchanged refreshes cause no collection changes; one status change updates only one device");

        using (var input = new InputProcessingThread())
        using (var signal = new ManualResetEventSlim())
        {
            var threadId = 0;
            using var backgroundDispatch = new InputDispatchQueue(input.Dispatcher, (_, _) =>
            {
                threadId = Environment.CurrentManagedThreadId;
                signal.Set();
            });
            backgroundDispatch.Enqueue(peer, new InputPacket(InputEventType.MouseWheel, 1));
            // Intentionally block the main/UI thread. Input must still be handled.
            if (!signal.Wait(TimeSpan.FromSeconds(2)) || threadId == Environment.CurrentManagedThreadId)
                throw new Exception("Input processing still depends on the UI thread");
            Console.WriteLine("PASS: dedicated input thread delivers events while the UI thread is blocked");
        }

        using (var input = new InputProcessingThread())
        using (var moved = new ManualResetEventSlim())
        {
            var wheelCalls = 0;
            using var dispatch = new InputDispatchQueue(input.Dispatcher, (_, p) =>
            {
                if (p.Type == InputEventType.MouseWheel)
                {
                    wheelCalls++;
                    var call = System.Diagnostics.Stopwatch.StartNew();
                    while (call.Elapsed.TotalMilliseconds < .25) Thread.SpinWait(10);
                }
                if (p.Type == InputEventType.MouseMove) moved.Set();
            });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            input.Invoke(() =>
            {
                for (var i = 0; i < 10000; i++)
                    dispatch.Enqueue(peer, WheelCapture.Stamp(new InputPacket(InputEventType.MouseWheel, 1), 1000));
                dispatch.Enqueue(peer, new InputPacket(InputEventType.MouseMove, 1, 1));
            });
            if (!moved.Wait(TimeSpan.FromMilliseconds(500)) || wheelCalls != 84)
                throw new Exception("Bounded scrolling starved the following pointer input");
            Console.WriteLine($"PASS: pointer delivered after 10,000-delta overload in {clock.ElapsedMilliseconds} ms, using {wheelCalls} bounded injections");
        }
        ScrollDiagnostics.Observe(ScrollStage.CaptureWindow, new InputPacket(InputEventType.MouseWheel, 7));
        ScrollDiagnostics.Observe(ScrollStage.Send, new InputPacket(InputEventType.Key, 65));
        using (var document = System.Text.Json.JsonDocument.Parse(ScrollDiagnostics.ExportJson()))
        {
            var records = document.RootElement.GetProperty("Records").EnumerateArray().ToArray();
            if (records.Length != 1 || records[0].GetProperty("Delta").GetInt32() != 7)
                throw new Exception("Diagnostics failed to record wheel sample or recorded keyboard input");
        }
        Console.WriteLine("PASS: wheel diagnostics export excludes keyboard input");

        // Native integration test is explicit because it briefly owns foreground
        // input and injects synthetic contacts into this test's capture window.
        if (args.Contains("--native")) NativeTest();
    }

    private static void ThreadIsolationTest(bool isolated)
    {
        // Exercise the real MainWindow wiring without Loaded/network connection,
        // without SendInput, and without modifying the user's saved settings.
        var app = new Application();
        var window = new MainWindow(new ClientSettings { IsolateInputInjection = isolated });
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var input = (InputProcessingThread)typeof(MainWindow).GetField("_inputThread", flags)!.GetValue(window)!;
        var hooks = (InputProcessingThread)typeof(MainWindow).GetField("_hookThread", flags)!.GetValue(window)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            if (ReferenceEquals(input, hooks) == isolated) throw new Exception("Incorrect thread wiring");
            var blocked = input.Dispatcher.InvokeAsync(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Test worker was not released");
            }).Task;
            if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new Exception("Input worker did not start");
            var ping = hooks.Dispatcher.InvokeAsync(() => { }).Task;
            try
            {
                var responsive = ping.Wait(TimeSpan.FromMilliseconds(500));
                if (responsive != isolated) throw new Exception("Unexpected hook responsiveness during injection stall");
            }
            finally { release.Set(); }
            if (!Task.WaitAll([blocked, ping], TimeSpan.FromSeconds(2))) throw new Exception("Input did not resume");
            using var json = System.Text.Json.JsonDocument.Parse(ScrollDiagnostics.ExportJson());
            if (json.RootElement.GetProperty("InjectionMode").GetString() != (isolated ? "isolated" : "shared"))
                throw new Exception("Exported mode does not match running mode");
            Console.WriteLine($"PASS: {(isolated ? "isolated hook loop stays responsive" : "shared loop reproduces worker contention")}; real window wiring and mode metadata");
        }
        finally
        {
            release.Set();
            // Finish shutdown before ending the test; ensure both owning loops exit.
            var stopped = (Task)typeof(MainWindow).GetMethod("StopInputThreadsAsync", flags)!.Invoke(window, null)!;
            if (!stopped.Wait(TimeSpan.FromSeconds(3))) throw new Exception("Input thread cleanup blocked");
            typeof(MainWindow).GetMethod("ExitApplication", flags)!.Invoke(window, null);
            input.Dispose();
            if (!ReferenceEquals(input, hooks)) hooks.Dispose();
            app.Shutdown();
        }
    }

    private static void CaptureDiagnostics()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var input = new InputProcessingThread();
        var window = input.Invoke(() => new RemoteInputWindow());
        var hooks = input.Invoke(() => new InputHookManager());
        var samples = new ConcurrentQueue<(long At, string Source, InputPacket Packet)>();
        window.PacketCaptured += packet => samples.Enqueue((System.Diagnostics.Stopwatch.GetTimestamp(), "window", packet));
        hooks.PacketCaptured += packet => samples.Enqueue((System.Diagnostics.Stopwatch.GetTimestamp(), "hook", packet));
        NativeMethods.GetCursorPos(out var cursor);
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                hooks.SetConnected(true); hooks.SetCanForward(true);
                hooks.StartForwarding();
                await input.Dispatcher.InvokeAsync(() => window.SetActive(true));
                await Task.Delay(150);
                if (!window.IsActive)
                {
                    Console.WriteLine("SKIP: capture diagnostics needs foreground activation; no native result obtained");
                    return;
                }
                foreach (var direction in new[] { SwipeDirection.Up, SwipeDirection.Down, SwipeDirection.Left })
                {
                    samples.Clear();
                    await SyntheticTouchpad.Swipe(direction, contacts: 2);
                    await Task.Delay(400);
                    var wheels = samples.Where(p => p.Packet.Type == InputEventType.MouseWheel).ToArray();
                    if (wheels.Length == 0) throw new Exception("No native scroll samples captured");
                    var gaps = wheels.Zip(wheels.Skip(1), (a, b) => (b.At - a.At) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).Order().ToArray();
                    var p95 = gaps.Length == 0 ? 0 : gaps[(int)((gaps.Length - 1) * .95)];
                    var maximum = gaps.Length == 0 ? 0 : gaps[^1];
                    Console.WriteLine($"CAPTURE {direction}: samples={wheels.Length}, window={wheels.Count(p => p.Source == "window")}, hook={wheels.Count(p => p.Source == "hook")}, total={wheels.Sum(p => (long)p.Packet.Value1)}, maxDelta={wheels.Max(p => Math.Abs(p.Packet.Value1))}, gapP95={p95:F2}ms, maxGap={maximum:F2}ms");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                await input.Dispatcher.InvokeAsync(() =>
                {
                    hooks.StopForwarding(); window.SetActive(false);
                    NativeMethods.SetCursorPos(cursor.X, cursor.Y);
                });
                app.Shutdown();
            }
        };
        app.Run();
        input.StopAsync(() => { hooks.Dispose(); window.Dispose(); }).GetAwaiter().GetResult();
        if (failure is not null) throw failure;
    }

    private static void NativeTest(bool gesturesOnly = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var input = new InputProcessingThread();
        var window = input.Invoke(() => new RemoteInputWindow());
        Console.WriteLine("Native gesture support: " + window.GestureStatus);
        if (!window.NativeGesturesSupported) { Console.WriteLine("SKIP: native touchpad gestures are unsupported"); input.Invoke(window.Dispose); app.Shutdown(); return; }
        Exception? failure = null;
        var captured = new ConcurrentQueue<InputPacket>();
        window.PacketCaptured += captured.Enqueue;
        var hooks = input.Invoke(() => new InputHookManager());
        hooks.SetConnected(true);
        hooks.SetCanForward(true);
        hooks.PacketCaptured += captured.Enqueue;
        var messages = new ConcurrentDictionary<int, int>();
        window.InputMessageObserved += message => messages.AddOrUpdate(message, 1, (_, count) => count + 1);
        NativeMethods.GetCursorPos(out var originalCursor);
        app.Startup += async (_, _) =>
        {
            try
            {
                await input.Dispatcher.InvokeAsync(() => window.SetActive(true));
                hooks.StartForwarding();
                await Task.Delay(650);
                if (!window.IsActive)
                {
                    Console.WriteLine("SKIP: foreground activation unavailable; native gestures not injected. " + window.GestureStatus);
                    return;
                }
                foreach (var direction in Enum.GetValues<SwipeDirection>())
                {
                    captured.Clear();
                    await SyntheticTouchpad.Swipe(direction);
                    await Task.Delay(150);
                    if (!captured.SequenceEqual(GestureShortcut.Packets(direction)))
                        throw new Exception($"Native {direction} swipe did not produce the expected remote shortcut (packets: {captured.Count})");
                    Console.WriteLine($"PASS: native synthetic three-finger {direction} -> remote shortcut only");
                }
                if (gesturesOnly) return;
                captured.Clear();
                messages.Clear();
                NativeMethods.SetCursorPos(400, 400);
                await Task.Delay(100);
                await SyntheticTouchpad.Swipe(SwipeDirection.Up, contacts: 2);
                await Task.Delay(250);
                if (!captured.Any(p => p.Type == InputEventType.MouseWheel)
                    || captured.Where(p => p.Type == InputEventType.MouseWheel).Sum(p => (long)p.Value1) == 0)
                    throw new Exception("Native two-finger pan was not captured as remote scrolling; messages=" + string.Join(",", messages.Select(p => $"{p.Key:X}:{p.Value}")));
                var scrollTotal = captured.Where(p => p.Type == InputEventType.MouseWheel).Sum(p => (long)p.Value1);
                if (Math.Abs(scrollTotal) < 120)
                    throw new Exception($"Windows-native two-finger scroll unexpectedly small: {scrollTotal}");
                Console.WriteLine($"PASS: Windows-native 24mm two-finger scroll -> {scrollTotal} wheel units (includes system acceleration/inertia)");
                foreach (var direction in new[] { SwipeDirection.Up, SwipeDirection.Down, SwipeDirection.Left, SwipeDirection.Right, SwipeDirection.Up })
                {
                    captured.Clear();
                    await SyntheticTouchpad.Swipe(direction, contacts: 2);
                    await Task.Delay(100);
                    var wheel = captured.Count(p => p.Type == InputEventType.MouseWheel);
                    if (wheel == 0) throw new Exception("Repeated scroll stopped producing wheel events");
                    captured.Clear();
                    NativeMethods.SendInput(1, [new NativeMethods.Input
                    {
                        Type = NativeMethods.InputMouse,
                        Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MouseInput { Dx = 5, Flags = NativeMethods.MouseeventfMove } },
                    }], Marshal.SizeOf<NativeMethods.Input>());
                    await Task.Delay(50);
                    if (!captured.Any(p => p.Type == InputEventType.MouseMove)) throw new Exception("Mouse motion did not resume after two-finger scrolling");
                }
                Console.WriteLine("PASS: repeated vertical/horizontal scrolls with real low-level hooks, followed by resumed mouse motion");
                captured.Clear();
                var duringUiBlock = Task.Run(async () =>
                {
                    await SyntheticTouchpad.Swipe(SwipeDirection.Down, contacts: 2);
                    await Task.Delay(150);
                    return captured.Any(p => p.Type == InputEventType.MouseWheel);
                });
                // Keep UI blocked for the entire gesture, not just before it.
                if (!duringUiBlock.GetAwaiter().GetResult()) throw new Exception("Touchpad scrolling stopped while UI was blocked");
                Console.WriteLine("PASS: native touchpad capture and low-level hooks stay responsive while main UI is blocked");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                await input.Dispatcher.InvokeAsync(() =>
                {
                    hooks.StopForwarding();
                    window.SetActive(false);
                    NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
                });
                app.Shutdown();
            }
        };
        app.Run();
        input.StopAsync(() => { hooks.Dispose(); window.Dispose(); }).GetAwaiter().GetResult();
        if (failure is not null) throw failure;
    }
}

internal static class SyntheticTouchpad
{
    public static async Task Swipe(SwipeDirection direction, int contacts = 3)
    {
        var parameters = new Parameters { Type = 5, Count = 5, Feedback = 3, Width = 10000, Height = 6000, Options = 3 };
        var device = CreateSyntheticPointerDevice2(ref parameters);
        if (device == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var points = new PointerTypeInfo[contacts];
            uint time = 1;
            for (var step = 0; step <= 13; step++)
            {
                for (var i = 0; i < contacts; i++)
                {
                    var amount = Math.Min(step, 12) * 200;
                    var x = (contacts == 2 ? 3000 + i * 4000 : 3500 + i * 700)
                        + (direction == SwipeDirection.Left ? -amount : direction == SwipeDirection.Right ? amount : 0);
                    var y = 3000 + (direction == SwipeDirection.Up ? -amount : direction == SwipeDirection.Down ? amount : 0);
                    points[i].Type = 5;
                    points[i].Pointer.Type = 5;
                    points[i].Pointer.Id = (uint)i;
                    points[i].Pointer.Flags = step == 13 ? 0x4000u : 0x4006u;
                    points[i].Pointer.Himetric = new NativeMethods.Point { X = x, Y = y };
                    points[i].Pointer.Time = time;
                }
                if (!InjectSyntheticPointerInput(device, points, (uint)contacts)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                await Task.Delay(10);
                time += 10;
            }
        }
        finally { DestroySyntheticPointerDevice(device); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateSyntheticPointerDevice2(ref Parameters parameters);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectSyntheticPointerInput(nint device, [In] PointerTypeInfo[] points, uint count);
    [DllImport("user32.dll")] private static extern void DestroySyntheticPointerDevice(nint device);
    [StructLayout(LayoutKind.Sequential)] private struct Parameters
    { public int Type; public uint Count; public int Feedback; public nint Monitor; public uint Width, Height, Options; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        public int Type; public uint Id, Frame, Flags; public nint Device, Window;
        public NativeMethods.Point Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, History; public int Data; public uint Keys; public ulong Performance; public int ButtonChange;
    }
    [StructLayout(LayoutKind.Explicit, Size = 152)] private struct PointerTypeInfo
    { [FieldOffset(0)] public int Type; [FieldOffset(8)] public PointerInfo Pointer; }
}
