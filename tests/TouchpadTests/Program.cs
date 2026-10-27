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
        var peer = Guid.NewGuid();
        var queue = new InputPacketQueue();
        for (var i = 0; i < 10000; i++) queue.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 1)));
        if (queue.Count != 1 || !queue.TryDequeue(out var sum) || sum.Packet.Value1 != 10000)
            throw new Exception("High-resolution wheel coalescing lost scroll distance");
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

        var playback = new ScrollPlaybackBuffer();
        var paced = new List<RoutedInput>();
        playback.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 480)), paced.Add);
        if (paced.Count != 0) throw new Exception("Relay wheel burst was injected before its playback clock");
        if (!playback.Tick(paced.Add) || playback.Tick(paced.Add)) throw new Exception("Relay wheel burst did not finish in two ticks");
        if (paced.Count != 2 || paced.Sum(p => p.Packet.Value1) != 480 || paced.Any(p => p.Packet.Value1 != 240))
            throw new Exception("Relay wheel burst distance was not preserved and distributed");
        paced.Clear();
        playback.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 120)), paced.Add);
        playback.Tick(paced.Add);
        playback.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, -120)), paced.Add);
        playback.Tick(paced.Add); playback.Tick(paced.Add);
        if (paced.Select(p => p.Packet.Value1).SequenceEqual(new[] { 60, 60, -60, -60 }) != true)
            throw new Exception("Scroll reversal reordered pending direction");
        playback.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 35)), paced.Add);
        playback.Flush(paced.Add);
        if (playback.HasPending || paced.Last().Packet.Value1 != 35) throw new Exception("Key/click ordering barrier did not flush wheel input");
        playback.Enqueue(new RoutedInput(peer, new InputPacket(InputEventType.MouseWheel, 120)), paced.Add);
        var beforeClear = paced.Count;
        playback.Clear(); playback.Tick(paced.Add);
        if (paced.Count != beforeClear) throw new Exception("Disconnected peer produced delayed scroll input");
        Console.WriteLine("PASS: relay scroll pacing, exact distance, reversal ordering, immediate barriers and disconnect cancellation");

        var dispatcher = Dispatcher.CurrentDispatcher;
        var delivered = new List<InputPacket>();
        using (var dispatch = new InputDispatchQueue(dispatcher, (_, packet) => delivered.Add(packet)))
        {
            for (var i = 0; i < 10000; i++) dispatch.Enqueue(peer, new InputPacket(InputEventType.MouseWheel, 1));
            dispatch.Enqueue(peer, new InputPacket(InputEventType.ReleaseAll));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (delivered.Count != 2 || delivered[0].Value1 != 10000 || delivered[1].Type != InputEventType.ReleaseAll)
                throw new Exception("UI dispatch batching or release ordering failed");
        }
        Console.WriteLine("PASS: 10,000 scroll events delivered to UI as one scroll plus ordered release");

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
        using (var complete = new ManualResetEventSlim())
        {
            var outputs = new ConcurrentQueue<int>();
            var smooth = input.Invoke(() => new ScrollPlayback(input.Dispatcher, item =>
            {
                outputs.Enqueue(item.Packet.Value1);
                if (outputs.Sum() == 480) complete.Set();
            }));
            input.Invoke(() => smooth.Enqueue(peer, new InputPacket(InputEventType.MouseWheel, 480)));
            if (!complete.Wait(TimeSpan.FromSeconds(2)) || outputs.Count != 2 || outputs.Any(p => p != 240))
                throw new Exception("High-resolution relay playback clock failed");
            input.StopAsync(smooth.Dispose).GetAwaiter().GetResult();
            Console.WriteLine("PASS: high-resolution relay playback clock delivers two ordered scroll slices without UI-thread work");
        }

        // Native integration test is explicit because it briefly owns foreground
        // input and injects synthetic contacts into this test's capture window.
        if (args.Contains("--native")) NativeTest();
    }

    private static void NativeTest()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var input = new InputProcessingThread();
        var window = input.Invoke(() => new RemoteInputWindow());
        Console.WriteLine("Native gesture support: " + window.GestureStatus);
        if (!window.NativeGesturesSupported) { input.Invoke(window.Dispose); app.Shutdown(); return; }
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
                await Task.Delay(150);
                if (!window.IsActive)
                {
                    Console.WriteLine("SKIP: native foreground-input integration; Windows did not grant this background test foreground activation");
                    return;
                }
                foreach (var direction in Enum.GetValues<SwipeDirection>())
                {
                    captured.Clear();
                    await SyntheticTouchpad.Swipe(direction);
                    await Task.Delay(150);
                    var keys = captured.Where(p => p.Type == InputEventType.Key).ToArray();
                    if (!keys.SequenceEqual(GestureShortcut.Packets(direction)))
                        throw new Exception($"Native {direction} swipe did not produce the expected remote shortcut (keys: {keys.Length}, other packets: {captured.Count - keys.Length}, foreground: {window.IsActive})");
                    Console.WriteLine($"PASS: native synthetic three-finger {direction} -> remote shortcut only");
                }
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
