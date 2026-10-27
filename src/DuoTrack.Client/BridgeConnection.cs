using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Diagnostics;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal sealed class BridgeConnection : IDisposable
{
    private readonly BridgeApiClient _api;
    private readonly string _deviceName;
    private readonly LanPeerTransport _lan;
    private readonly InputPacketQueue _outgoing = new();
    private readonly Channel<byte> _outgoingWake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, bool> _sessionRoutes = new(); // true = LAN
    private readonly ConcurrentDictionary<Guid, byte> _interrupted = new();
    private readonly object _stateLock = new();
    private DeviceInfo[] _serverDevices = [];
    private ClientWebSocket? _relaySocket;
    private Task? _runTask;
    private Task? _sendTask;
    private TargetSelection? _target;
    public Guid? CurrentTargetDeviceId => Volatile.Read(ref _target)?.Id;
    private readonly InputPerformance _performance = new();
    public InputPerformanceSnapshot Performance => _performance.Snapshot();
    private volatile bool _relayConnected;
    private volatile bool _connected;

    public bool IsConnected => _connected;
    public bool IsRelayConnected => _relayConnected;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? StatusChanged;
    public event Action? DevicesChanged;
    public event Action<Guid>? PeerConnectionLost;
    public event Action<Guid, InputPacket>? PacketReceived;

    public BridgeConnection(BridgeApiClient api, string deviceName, DeviceIdentity identity, LanPeerTransport? lan = null)
    {
        _api = api;
        _deviceName = deviceName;
        _lan = lan ?? new LanPeerTransport(identity, api.TrustedPeers);
        _lan.PacketReceived += (id, packet) => OnPacket(id, packet, direct: true);
        _lan.Diagnostic += message => StatusChanged?.Invoke(message);
        _lan.PeerChanged += (id, connected) =>
        {
            if (!connected && _sessionRoutes.TryGetValue(id, out var direct) && direct) InterruptSession(id);
            UpdateConnectivity();
            DevicesChanged?.Invoke();
            if (connected) StatusChanged?.Invoke("局域网直连已就绪，新的控制会话优先使用直连");
        };
    }

    public void Start()
    {
        if (_runTask is not null) return;
        try { _lan.Start(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        { StatusChanged?.Invoke($"局域网监听不可用：{ex.Message}，正在使用公网中继"); }
        _sendTask = Task.Run(() => SendLoopAsync(_shutdown.Token));
        _runTask = Task.Run(() => RunRelayAsync(_shutdown.Token));
    }

    public IReadOnlyList<DeviceInfo> GetDevices()
    {
        var remote = _serverDevices.ToDictionary(d => d.Id);
        foreach (var peer in _api.TrustedPeers.All)
            if (!remote.ContainsKey(peer.Id)) remote[peer.Id] = new DeviceInfo(peer.Id, peer.Name, false, peer.PublicKey);
        return remote.Values.Select(device => device with
        {
            Online = _lan.IsConnected(device.Id) || (_relayConnected && device.Online),
            ConnectionMode = _lan.IsConnected(device.Id)
                ? _sessionRoutes.TryGetValue(device.Id, out var direct) && !direct
                    ? "公网中继（直连就绪）" : "局域网直连"
                : "公网中继",
        }).OrderBy(d => d.Name).ToArray();
    }

    public void SetTarget(Guid? deviceId)
    {
        if (CurrentTargetDeviceId is { } oldTarget && oldTarget != deviceId)
            SendTo(oldTarget, new InputPacket(InputEventType.ReleaseAll));
        Volatile.Write(ref _target, deviceId is { } id ? new TargetSelection(id) : null);
    }

    public void Send(InputPacket packet)
    {
        if (CurrentTargetDeviceId is { } target) SendTo(target, packet);
    }

    public void SendTo(Guid target, InputPacket packet)
    {
        if (_shutdown.IsCancellationRequested) return;
        _performance.ObserveQueue(_outgoing.Enqueue(new RoutedInput(target, packet, Stopwatch.GetTimestamp())));
        _outgoingWake.Writer.TryWrite(0);
    }

    private void OnPacket(Guid source, InputPacket packet, bool direct)
    {
        if (packet.Type == InputEventType.EnterScreen)
        {
            _sessionRoutes[source] = direct;
            _interrupted.TryRemove(source, out _);
        }
        else if (_sessionRoutes.TryGetValue(source, out var currentRoute) && currentRoute != direct) return;
        PacketReceived?.Invoke(source, packet);
        if (packet.Type == InputEventType.ReleaseAll) _sessionRoutes.TryRemove(source, out _);
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        var batchItems = new RoutedInput[64];
        var batchPackets = new InputPacket[64];
        try
        {
            await foreach (var wake in _outgoingWake.Reader.ReadAllAsync(token))
            {
                for (var batch = 0; batch < 256 && _outgoing.TryDequeue(out var item); batch++)
                {
                    var (target, packet, _) = item;
                    if (packet.Type == InputEventType.EnterScreen)
                    {
                        _sessionRoutes[target] = _lan.IsConnected(target);
                        _interrupted.TryRemove(target, out _);
                    }
                    if (_interrupted.ContainsKey(target) && packet.Type != InputEventType.ReleaseAll) continue;
                    var direct = _sessionRoutes.TryGetValue(target, out var route) ? route : _lan.IsConnected(target);
                    var count = 1;
                    batchItems[0] = item;
                    batchPackets[0] = packet;
                    if (direct && packet.Type != InputEventType.ReleaseAll)
                    {
                        while (count < batchItems.Length && _outgoing.TryDequeueFollowing(target, out var next))
                        {
                            batchItems[count] = next;
                            batchPackets[count++] = next.Packet;
                            if (next.Packet.Type == InputEventType.ReleaseAll) break;
                        }
                    }
                    for (var i = 0; i < count; i++) _performance.ObserveDelivery(batchItems[i].QueuedAt);
                    var sent = false;
                    if (direct) sent = await _lan.SendBatchAsync(target, batchPackets.AsMemory(0, count), token);
                    else if (_relaySocket is { State: WebSocketState.Open } socket)
                    {
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(3));
                            await socket.SendAsync(RelayWire.EncodeInput(target, packet), WebSocketMessageType.Binary, true, timeout.Token);
                            sent = true;
                        }
                        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                    }
                    if (sent) _performance.ObserveFlush();
                    // Never replay a possibly delivered event on another transport.
                    // Release this session; the next crossing chooses a fresh route.
                    if (!sent && packet.Type != InputEventType.ReleaseAll) InterruptSession(target);
                    if (batchPackets[count - 1].Type == InputEventType.ReleaseAll) _sessionRoutes.TryRemove(target, out _);
                }
                if (_outgoing.Count > 0) _outgoingWake.Writer.TryWrite(0);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void InterruptSession(Guid id)
    {
        _sessionRoutes.TryRemove(id, out _);
        if (_interrupted.TryAdd(id, 0)) PeerConnectionLost?.Invoke(id);
    }

    private async Task RunRelayAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_lan.HasPeers) StatusChanged?.Invoke("正在连接服务器，同时搜索局域网设备…");
                await _api.AuthenticateAsync(_deviceName, token);
                _serverDevices = (await _api.GetDevicesAsync(token)).ToArray();
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.SetRequestHeader("Authorization", $"Bearer {_api.SessionToken}");
                await socket.ConnectAsync(_api.BuildWebSocketUri(), token);
                _relaySocket = socket;
                _relayConnected = true;
                UpdateConnectivity();
                DevicesChanged?.Invoke();
                StatusChanged?.Invoke(_lan.HasPeers ? "优先使用局域网直连，公网中继可备用" : "公网中继已连接，正在搜索局域网直连");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                var receive = ReceiveRelayAsync(socket, linked.Token);
                var refresh = RefreshPeersAsync(linked.Token);
                await Task.WhenAny(receive, refresh);
                linked.Cancel();
                try { await Task.WhenAll(receive, refresh); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(_lan.HasPeers ? "局域网直连可用；公网中继暂不可用"
                    : $"公网中继连接失败：{ex.Message}；继续搜索局域网设备");
            }
            finally
            {
                _relaySocket = null;
                _relayConnected = false;
                foreach (var route in _sessionRoutes.Where(p => !p.Value).ToArray()) InterruptSession(route.Key);
                UpdateConnectivity();
                DevicesChanged?.Invoke();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshPeersAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            try
            {
                _serverDevices = (await _api.GetDevicesAsync(token)).ToArray();
                DevicesChanged?.Invoke();
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TaskCanceledException)
            { if (token.IsCancellationRequested) throw; }
        }
    }

    private async Task ReceiveRelayAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType == WebSocketMessageType.Binary && result.EndOfMessage &&
                RelayWire.TryDecodeInput(buffer.AsSpan(0, result.Count), out var source, out var packet))
                OnPacket(source, packet, direct: false);
        }
    }

    private void UpdateConnectivity()
    {
        lock (_stateLock)
        {
            var connected = _relayConnected || _lan.HasPeers;
            if (_connected == connected) return;
            _connected = connected;
            ConnectionChanged?.Invoke(connected);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _lan.Dispose();
        _relaySocket?.Abort();
        _outgoingWake.Writer.TryComplete();
        _outgoing.Clear();
    }

    private sealed record TargetSelection(Guid Id);
}
