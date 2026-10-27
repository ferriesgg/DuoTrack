using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using DuoTrack.Client;
using DuoTrack.Protocol;

static async Task WaitFor(Func<bool> predicate, string failure, int seconds = 8)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    while (!predicate())
    {
        if (timeout.IsCancellationRequested) throw new Exception(failure);
        await Task.Delay(25);
    }
}

var directory = Path.Combine(Path.GetFullPath("test-data"), "transport-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
using var first = DeviceIdentity.CreateEphemeral();
using var second = DeviceIdentity.CreateEphemeral();
using var stranger = DeviceIdentity.CreateEphemeral();
var storeA = new TrustedPeerStore("test", Path.Combine(directory, "first.json"));
var storeB = new TrustedPeerStore("test", Path.Combine(directory, "second.json"));
storeA.Update([new DeviceInfo(second.DeviceId, "B", true, second.PublicKey)], true);
storeB.Update([new DeviceInfo(first.DeviceId, "A", true, first.PublicKey)], true);
// Reload from disk: no relay is involved in this entire test.
storeA = new TrustedPeerStore("test", Path.Combine(directory, "first.json"));
storeB = new TrustedPeerStore("test", Path.Combine(directory, "second.json"));
using var lanA = new LanPeerTransport(first, storeA, 0, false);
using var lanB = new LanPeerTransport(second, storeB, 0, false);
lanA.Diagnostic += Console.WriteLine;
lanB.Diagnostic += Console.WriteLine;
lanA.Start(); lanB.Start();
var receivedA = new ConcurrentQueue<InputPacket>();
var receivedB = new ConcurrentQueue<InputPacket>();
lanA.PacketReceived += (id, packet) => { if (id != second.DeviceId) throw new Exception("Wrong peer A"); receivedA.Enqueue(packet); };
lanB.PacketReceived += (id, packet) => { if (id != first.DeviceId) throw new Exception("Wrong peer B"); receivedB.Enqueue(packet); };
await lanA.ConnectPeerAsync(second.DeviceId, new IPEndPoint(IPAddress.Loopback, lanB.ListenPort));
await WaitFor(() => lanA.IsConnected(second.DeviceId) && lanB.IsConnected(first.DeviceId), "Mutual TLS LAN connection failed");
var enter = new InputPacket(InputEventType.EnterScreen, 1, 7500, 1);
if (!await lanA.SendAsync(second.DeviceId, enter, CancellationToken.None)) throw new Exception("LAN send failed");
for (var i = 0; i < 500; i++)
    if (!await lanA.SendAsync(second.DeviceId, new InputPacket(InputEventType.MouseMove, i, -i), CancellationToken.None))
        throw new Exception("Motion send failed");
var returnControl = new InputPacket(InputEventType.ReturnControl, 10, 7500);
await lanB.SendAsync(first.DeviceId, returnControl, CancellationToken.None);
await WaitFor(() => receivedB.Count == 501 && receivedA.Count == 1, "Duplex LAN input delivery failed");
if (!receivedB.TryDequeue(out var actualEnter) || actualEnter != enter) throw new Exception("EnterScreen was changed");
for (var i = 0; i < 500; i++)
    if (!receivedB.TryDequeue(out var move) || move.Value1 != i || move.Value2 != -i) throw new Exception("Input ordering failed");
if (!receivedA.TryDequeue(out var actualReturn) || actualReturn != returnControl) throw new Exception("ReturnControl was changed");
Console.WriteLine("PASS: persisted peer trust, mutual TLS, 500 ordered movements, bidirectional screen transitions without relay");

var strangerStore = new TrustedPeerStore("test", Path.Combine(directory, "stranger.json"));
strangerStore.Update([new DeviceInfo(second.DeviceId, "B", true, second.PublicKey)], true);
using var rogue = new LanPeerTransport(stranger, strangerStore, 0, false);
rogue.Start();
await rogue.ConnectPeerAsync(second.DeviceId, new IPEndPoint(IPAddress.Loopback, lanB.ListenPort));
await Task.Delay(250);
if (rogue.IsConnected(second.DeviceId) || lanB.IsConnected(stranger.DeviceId)) throw new Exception("Unpaired peer accepted");
using var wrongCertificate = stranger.CreateLanCertificate();
if (storeA.Authenticate(wrongCertificate, second.DeviceId) is not null) throw new Exception("Impersonated device accepted");
Console.WriteLine("PASS: unpaired client and incorrect peer certificate rejected");

var lossObserved = false;
lanA.PeerChanged += (id, online) => { if (id == second.DeviceId && !online) lossObserved = true; };
lanB.Dispose();
await WaitFor(() => lossObserved && !lanA.IsConnected(second.DeviceId), "Peer disconnect was not detected");
Console.WriteLine("PASS: disconnect detected and peer removed");

// Occupied listening port must fall back to an ephemeral local port.
using var occupied = new TcpListener(IPAddress.Any, 0);
occupied.Start();
using var fallback = new LanPeerTransport(stranger, strangerStore, ((IPEndPoint)occupied.LocalEndpoint).Port, false);
fallback.Start();
if (fallback.ListenPort == ((IPEndPoint)occupied.LocalEndpoint).Port) throw new Exception("Port fallback failed");
Console.WriteLine("PASS: occupied LAN port fallback");

// Run the actual BridgeConnection with the server deliberately unreachable.
using var apiA = new BridgeApiClient(first, Path.Combine(directory, "bridgeA.json"));
using var apiB = new BridgeApiClient(second, Path.Combine(directory, "bridgeB.json"));
apiA.Configure("http://127.0.0.1:1/test-" + Guid.NewGuid().ToString("N"));
apiB.Configure("http://127.0.0.1:1/test-" + Guid.NewGuid().ToString("N"));
apiA.TrustedPeers.Update([new DeviceInfo(second.DeviceId, "B", false, second.PublicKey)], true);
apiB.TrustedPeers.Update([new DeviceInfo(first.DeviceId, "A", false, first.PublicKey)], true);
using var bridgeLanA = new LanPeerTransport(first, apiA.TrustedPeers, 0, false);
using var bridgeLanB = new LanPeerTransport(second, apiB.TrustedPeers, 0, false);
using var bridgeA = new BridgeConnection(apiA, "A", first, bridgeLanA);
using var bridgeB = new BridgeConnection(apiB, "B", second, bridgeLanB);
var bridgePackets = new ConcurrentQueue<InputPacket>();
var directContexts = new ConcurrentQueue<bool>();
bridgeB.PacketReceived += (_, packet) => bridgePackets.Enqueue(packet);
bridgeB.InputReceived += (_, _, direct) => directContexts.Enqueue(direct);
bridgeA.Start(); bridgeB.Start();
await bridgeLanA.ConnectPeerAsync(second.DeviceId, new IPEndPoint(IPAddress.Loopback, bridgeLanB.ListenPort));
await WaitFor(() => bridgeA.IsConnected && bridgeB.IsConnected, "Offline bridge did not become connected");
if (bridgeA.IsRelayConnected || bridgeB.IsRelayConnected) throw new Exception("Unexpected relay connection");
bridgeA.SetTarget(second.DeviceId);
bridgeA.Send(enter);
bridgeA.Send(new InputPacket(InputEventType.Key, 65));
bridgeA.Send(new InputPacket(InputEventType.ReleaseAll));
await WaitFor(() => bridgePackets.Count == 3, "Offline bridge input failed");
await WaitFor(() => directContexts.Count == 3, "Transport context was not delivered");
if (directContexts.Any(d => !d)) throw new Exception("LAN input was mislabeled as relay input");
if (bridgeA.GetDevices().Single().ConnectionMode != "局域网直连") throw new Exception("Wrong UI route label");
Console.WriteLine("PASS: actual BridgeConnection controls a cached peer while public server is unreachable");
while (bridgePackets.TryDequeue(out _)) { }
bridgeA.Send(enter);
for (var i = 0; i < 4000; i++) bridgeA.Send(new InputPacket(InputEventType.MouseWheel, 1));
var shortcuts = GestureShortcut.Packets(SwipeDirection.Up);
foreach (var key in shortcuts) bridgeA.Send(key);
bridgeA.Send(new InputPacket(InputEventType.ReleaseAll));
await WaitFor(() => bridgePackets.Any(p => p.Type == InputEventType.ReleaseAll), "Batched scrolling did not finish");
var wheelPackets = bridgePackets.Where(p => p.Type == InputEventType.MouseWheel).ToArray();
if (wheelPackets.Sum(p => (long)p.Value1) != 4000 || wheelPackets.Length >= 4000)
    throw new Exception("LAN scroll coalescing lost precision or did not merge events");
if (!bridgePackets.Where(p => p.Type == InputEventType.Key).SequenceEqual(shortcuts))
    throw new Exception("LAN gesture shortcut order changed");
Console.WriteLine($"PASS: 4,000 precision scroll deltas sent as {wheelPackets.Length} LAN packets, followed by ordered gesture shortcut and release");
while (bridgePackets.TryDequeue(out _)) { }
var beforeBatch = bridgeA.Performance;
bridgeA.Send(enter);
for (var i = 0; i < 2048; i++) bridgeA.Send(new InputPacket(InputEventType.MouseMove, i, -i));
bridgeA.Send(new InputPacket(InputEventType.ReleaseAll));
await WaitFor(() => bridgePackets.Any(p => p.Type == InputEventType.ReleaseAll), "Batched movement did not finish");
var orderedMoves = bridgePackets.Where(p => p.Type == InputEventType.MouseMove).ToArray();
if (orderedMoves.Length != 2048 || orderedMoves.Where((p, i) => p.Value1 != i || p.Value2 != -i).Any())
    throw new Exception("TLS batching changed the mouse path");
var afterBatch = bridgeA.Performance;
var flushes = afterBatch.Flushes - beforeBatch.Flushes;
if (flushes >= 2048) throw new Exception("No TLS batching occurred under burst load");
Console.WriteLine($"PASS: 2,048 ordered movement packets in {flushes} TLS writes; source queue P95={afterBatch.P95Ms:F2}ms, average={afterBatch.AverageMs:F2}ms");

TaskCompletionSource<long>? wheelReceipt = null;
bridgeB.PacketReceived += (_, packet) =>
{
    if (packet.Type == InputEventType.MouseWheel) Volatile.Read(ref wheelReceipt)?.TrySetResult(Stopwatch.GetTimestamp());
};
bridgeA.Send(enter);
var oldPolicy = new List<double>();
var immediate = new List<double>();
for (var sample = 0; sample < 25; sample++)
{
    foreach (var simulateOldWait in new[] { true, false })
    {
        var receipt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref wheelReceipt, receipt);
        var started = Stopwatch.GetTimestamp();
        if (simulateOldWait) await Task.Delay(6); // Previous batching policy, for comparison only.
        bridgeA.Send(new InputPacket(InputEventType.MouseWheel, 1));
        var receivedAt = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (sample >= 5)
            (simulateOldWait ? oldPolicy : immediate).Add((receivedAt - started) * 1000.0 / Stopwatch.Frequency);
    }
}
bridgeA.Send(new InputPacket(InputEventType.ReleaseAll));
static double P95(List<double> samples) => samples.Order().ElementAt((int)Math.Ceiling(samples.Count * 0.95) - 1);
Console.WriteLine($"MEASURE: local-loopback isolated scroll first-packet P95: previous 6ms-wait policy={P95(oldPolicy):F2}ms; immediate-send={P95(immediate):F2}ms (not a real-LAN latency guarantee)");

// Exercise actual broadcast discovery, not only an explicitly provided endpoint.
if (System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
    n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
    n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
    n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)))
{
    using var discoveredA = new LanPeerTransport(first, storeA, 0);
    using var discoveredB = new LanPeerTransport(second, storeB, 0);
    discoveredA.Diagnostic += Console.WriteLine;
    discoveredB.Diagnostic += Console.WriteLine;
    discoveredA.Start(); discoveredB.Start();
    await WaitFor(() => discoveredA.IsConnected(second.DeviceId) && discoveredB.IsConnected(first.DeviceId),
        "Automatic LAN broadcast discovery failed (check local firewall)", 15);
    Console.WriteLine("PASS: actual IPv4 broadcast discovery and automatic mutual-TLS direct connection");
}

if (args.Length > 0)
{
    using var relayApiA = new BridgeApiClient(first, Path.Combine(directory, "relayA.json"));
    using var relayApiB = new BridgeApiClient(second, Path.Combine(directory, "relayB.json"));
    relayApiA.Configure(args[0]); relayApiB.Configure(args[0]);
    using var relayLanA = new LanPeerTransport(first, relayApiA.TrustedPeers, 0, false);
    using var relayLanB = new LanPeerTransport(second, relayApiB.TrustedPeers, 0, false);
    using var mixedA = new BridgeConnection(relayApiA, "LAN fallback A", first, relayLanA);
    using var mixedB = new BridgeConnection(relayApiB, "LAN fallback B", second, relayLanB);
    var mixedPackets = new ConcurrentQueue<InputPacket>();
    var lost = false;
    mixedB.PacketReceived += (_, packet) => mixedPackets.Enqueue(packet);
    mixedA.PeerConnectionLost += _ => { lost = true; mixedA.Send(new InputPacket(InputEventType.ReleaseAll)); };
    mixedA.Start(); mixedB.Start();
    await WaitFor(() => mixedA.IsRelayConnected && mixedB.IsRelayConnected, "Relay authentication failed", 12);
    var code = await relayApiA.CreatePairingCodeAsync(CancellationToken.None);
    await relayApiB.JoinPairingAsync(code.Code, CancellationToken.None);
    await WaitFor(() => mixedA.GetDevices().Any(d => d.Id == second.DeviceId && d.Online), "Paired relay peer not listed");
    mixedA.SetTarget(second.DeviceId);
    mixedA.Send(enter);
    mixedA.Send(new InputPacket(InputEventType.MouseMove, 5, 6));
    mixedA.Send(new InputPacket(InputEventType.ReleaseAll));
    await WaitFor(() => mixedPackets.Count == 3, "Relay-only bridge routing failed");
    if (mixedA.GetDevices().Single().ConnectionMode != "公网中继") throw new Exception("Relay route label failed");
    await relayLanA.ConnectPeerAsync(second.DeviceId, new IPEndPoint(IPAddress.Loopback, relayLanB.ListenPort));
    await WaitFor(() => relayLanA.HasPeers && relayLanB.HasPeers, "LAN upgrade failed");
    mixedA.Send(enter);
    mixedA.Send(new InputPacket(InputEventType.MouseMove, 7, 8));
    await WaitFor(() => mixedPackets.Count == 5, "LAN preferred route failed");
    if (mixedA.GetDevices().Single().ConnectionMode != "局域网直连") throw new Exception("LAN route not preferred");
    relayLanB.Dispose();
    await WaitFor(() => lost && !relayLanA.HasPeers, "Active LAN disconnect not reported");
    mixedA.Send(enter);
    mixedA.Send(new InputPacket(InputEventType.MouseMove, 9, 10));
    mixedA.Send(new InputPacket(InputEventType.ReleaseAll));
    await WaitFor(() => mixedPackets.Any(p => p.Type == InputEventType.MouseMove && p.Value1 == 9), "Relay fallback after direct connection loss failed");
    if (mixedA.GetDevices().Single().ConnectionMode != "公网中继") throw new Exception("Fallback route label failed");
    Console.WriteLine("PASS: actual relay routing, LAN preference, active direct disconnect and next-session relay fallback");
    await WaitFor(() => mixedA.RelayRttMs > 0 && mixedB.RelayRttMs > 0, "Relay RTT echo was not negotiated");
    while (mixedPackets.TryDequeue(out _)) { }
    mixedA.Send(enter);
    for (var i = 0; i < 2048; i++) mixedA.Send(new InputPacket(InputEventType.MouseMove, i, -i));
    mixedA.Send(new InputPacket(InputEventType.ReleaseAll));
    await WaitFor(() => mixedPackets.Any(p => p.Type == InputEventType.ReleaseAll), "Relay batched movement did not finish");
    var relayMoves = mixedPackets.Where(p => p.Type == InputEventType.MouseMove).ToArray();
    if (relayMoves.Length != 2048 || relayMoves.Where((p, i) => p.Value1 != i || p.Value2 != -i).Any())
        throw new Exception("Relay batch forwarding changed input ordering");
    Console.WriteLine($"PASS: negotiated relay batches preserve 2,048 movement events; relay RTT={mixedA.RelayRttMs:F2}ms");

    using var playbackThread = new InputProcessingThread();
    var played = new ConcurrentQueue<(long At, InputPacket Packet)>();
    using var finished = new ManualResetEventSlim();
    var smoothing = playbackThread.Invoke(() => new ScrollPlayback(playbackThread.Dispatcher,
        item => played.Enqueue((Stopwatch.GetTimestamp(), item.Packet))));
    using var playbackInput = new InputDispatchQueue(playbackThread.Dispatcher, item =>
    {
        if (item.Packet.Type == InputEventType.MouseWheel && item.Direct != true) smoothing.Enqueue(item.Peer, item.Packet);
        else
        {
            smoothing.Flush();
            played.Enqueue((Stopwatch.GetTimestamp(), item.Packet));
            if (item.Packet.Type == InputEventType.ReleaseAll) finished.Set();
        }
    });
    Action<Guid, InputPacket, bool> receiver = (id, packet, direct) => playbackInput.Enqueue(id, packet, direct);
    mixedB.InputReceived += receiver;
    mixedA.Send(enter);
    for (var wave = 0; wave < 20; wave++)
    {
        for (var i = 0; i < 8; i++) mixedA.Send(new InputPacket(InputEventType.MouseWheel, 15));
        await Task.Delay(wave % 5 == 4 ? 24 : 8); // Deliberately uneven relay bursts.
    }
    mixedA.Send(new InputPacket(InputEventType.Key, 65));
    mixedA.Send(new InputPacket(InputEventType.Key, 65, Flags: NativeMethods.KeyeventfKeyUp));
    mixedA.Send(new InputPacket(InputEventType.ReleaseAll));
    if (!finished.Wait(TimeSpan.FromSeconds(3))) throw new Exception("Continuous relay scroll did not drain");
    var completed = played.ToArray();
    if (completed.Where(p => p.Packet.Type == InputEventType.MouseWheel).Sum(p => (long)p.Packet.Value1) != 2400)
        throw new Exception("Relay playback changed total scroll distance");
    var keyIndex = Array.FindIndex(completed, p => p.Packet.Type == InputEventType.Key);
    if (completed.Skip(keyIndex).Any(p => p.Packet.Type == InputEventType.MouseWheel)) throw new Exception("Delayed wheel overtook keyboard input");
    var countAfterRelease = played.Count;
    await Task.Delay(50);
    if (played.Count != countAfterRelease) throw new Exception("Wheel playback continued after release");
    mixedB.InputReceived -= receiver;
    playbackThread.StopAsync(smoothing.Dispose).GetAwaiter().GetResult();
    Console.WriteLine("PASS: 20 uneven relay scroll bursts preserve 2,400 wheel units, key order and no delayed input after release");
}
