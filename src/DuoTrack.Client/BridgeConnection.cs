using System.Net.WebSockets;
using System.Threading.Channels;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal sealed class BridgeConnection(
    BridgeApiClient api,
    string deviceName) : IDisposable
{
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runTask;
    private Guid? _targetDeviceId;

    public bool IsConnected { get; private set; }
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? StatusChanged;
    public event Action<Guid, InputPacket>? PacketReceived;

    public void Start() => _runTask ??= Task.Run(() => RunAsync(_shutdown.Token));

    public void SetTarget(Guid? deviceId)
    {
        if (_targetDeviceId is { } oldTarget && oldTarget != deviceId)
        {
            _outgoing.Writer.TryWrite(RelayWire.EncodeInput(oldTarget, new InputPacket(InputEventType.ReleaseAll)));
        }
        _targetDeviceId = deviceId;
    }

    public void Send(InputPacket packet)
    {
        if (IsConnected && _targetDeviceId is { } target)
        {
            _outgoing.Writer.TryWrite(RelayWire.EncodeInput(target, packet));
        }
    }

    public void SendTo(Guid target, InputPacket packet)
    {
        if (IsConnected) _outgoing.Writer.TryWrite(RelayWire.EncodeInput(target, packet));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke("正在验证设备身份…");
                await api.AuthenticateAsync(deviceName, cancellationToken);

                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.SetRequestHeader("Authorization", $"Bearer {api.SessionToken}");
                await socket.ConnectAsync(api.BuildWebSocketUri(), cancellationToken);

                SetConnected(true);
                StatusChanged?.Invoke("已连接");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receiveTask = ReceiveAsync(socket, linked.Token);
                var sendTask = SendAsync(socket, linked.Token);
                await Task.WhenAny(receiveTask, sendTask);
                linked.Cancel();
                try { await Task.WhenAll(receiveTask, sendTask); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"连接失败：{FriendlyMessage(ex)}，2 秒后重试");
            }
            finally
            {
                SetConnected(false);
                while (_outgoing.Reader.TryRead(out _)) { }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Binary && result.EndOfMessage &&
                RelayWire.TryDecodeInput(buffer.AsSpan(0, result.Count), out var source, out var packet))
            {
                PacketReceived?.Invoke(source, packet);
            }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        await foreach (var payload in _outgoing.Reader.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        HttpRequestException => "无法访问服务器",
        WebSocketException => "WebSocket 连接中断",
        _ => exception.Message,
    };

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _shutdown.Dispose();
    }
}
