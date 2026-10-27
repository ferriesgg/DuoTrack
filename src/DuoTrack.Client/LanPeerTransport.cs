using System.Collections.Concurrent;
using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DuoTrack.Protocol;

namespace DuoTrack.Client;

internal sealed class LanPeerTransport : IDisposable
{
    internal const int DiscoveryPort = 45731;
    internal const int DefaultPort = 45732;
    private readonly DeviceIdentity _identity;
    private readonly TrustedPeerStore _trust;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentDictionary<Guid, PeerLink> _links = new();
    private readonly ConcurrentDictionary<Guid, byte> _dialing = new();
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();
    private readonly SemaphoreSlim _handshakes = new(8);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener _listener;
    private UdpClient? _discovery;
    private readonly bool _enableDiscovery;
    private int _disposed;

    public event Action<Guid, InputPacket>? PacketReceived;
    public event Action<Guid, bool>? PeerChanged;
    public event Action<string>? Diagnostic;
    public int ListenPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool HasPeers => !_links.IsEmpty;
    public bool IsConnected(Guid id) => _links.ContainsKey(id);

    public LanPeerTransport(DeviceIdentity identity, TrustedPeerStore trust, int port = DefaultPort, bool enableDiscovery = true)
    {
        _identity = identity;
        _trust = trust;
        _enableDiscovery = enableDiscovery;
        _certificate = identity.CreateLanCertificate();
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        try { _listener.Start(16); }
        catch (SocketException)
        {
            _listener.Stop();
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start(16);
        }
        Track(AcceptLoopAsync());
        if (_enableDiscovery)
        {
            try
            {
                _discovery = new UdpClient(AddressFamily.InterNetwork);
                _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _discovery.EnableBroadcast = true;
                _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                Track(DiscoverLoopAsync());
                Track(AnnounceLoopAsync());
            }
            catch (SocketException ex)
            {
                _discovery?.Dispose();
                _discovery = null;
                Diagnostic?.Invoke($"局域网发现不可用：{ex.SocketErrorCode}");
            }
        }
    }

    private void Track(Task task)
    {
        _tasks[task] = 0;
        _ = task.ContinueWith(completed =>
        {
            _tasks.TryRemove(completed, out _);
            _ = completed.Exception;
        }, TaskScheduler.Default);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_handshakes.Wait(0)) { client.Dispose(); continue; }
                Track(AcceptPeerAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task AcceptPeerAsync(TcpClient client)
    {
        SslStream? tls = null;
        try
        {
            client.NoDelay = true;
            Guid? peerId = null;
            tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            {
                peerId = _trust.Authenticate(certificate);
                return peerId is not null && peerId != _identity.DeviceId;
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
            if (peerId is not { } id || !_trust.Contains(id)) throw new AuthenticationException();
            Attach(id, client, tls);
            tls = null; // Ownership transferred to PeerLink.
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            Diagnostic?.Invoke($"局域网接入验证未完成：{ex.Message}");
            client.Dispose();
        }
        finally { tls?.Dispose(); _handshakes.Release(); }
    }

    internal async Task ConnectPeerAsync(Guid peerId, IPEndPoint endpoint)
    {
        if (!_trust.Contains(peerId) || IsConnected(peerId) || !_dialing.TryAdd(peerId, 0)) return;
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? tls = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token);
            tls = new SslStream(client.GetStream(), false,
                (_, certificate, _, _) => _trust.Authenticate(certificate, peerId) == peerId);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = $"DuoTrack-{peerId:D}",
                ClientCertificates = new X509CertificateCollection { _certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
            if (!_trust.Contains(peerId)) throw new AuthenticationException();
            Attach(peerId, client, tls);
            tls = null;
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            Diagnostic?.Invoke($"局域网直连尝试未完成：{ex.Message}");
            client.Dispose();
        }
        finally { tls?.Dispose(); _dialing.TryRemove(peerId, out _); }
    }

    private void Attach(Guid id, TcpClient client, SslStream tls)
    {
        var key = _trust.Find(id)?.PublicKey;
        if (key is null) { tls.Dispose(); client.Dispose(); return; }
        var link = new PeerLink(client, tls, key);
        if (_stop.IsCancellationRequested || !_links.TryAdd(id, link)) { link.Dispose(); return; }
        PeerChanged?.Invoke(id, true);
        Track(ReadPeerAsync(id, link));
        Track(HeartbeatAsync(id, link));
    }

    public async Task<bool> SendAsync(Guid id, InputPacket packet, CancellationToken token)
        => await SendBatchAsync(id, new[] { packet }, token);

    public async Task<bool> SendBatchAsync(Guid id, ReadOnlyMemory<InputPacket> packets, CancellationToken token)
    {
        if (packets.Length is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(packets));
        if (!_links.TryGetValue(id, out var link)) return false;
        const int frameSize = 1 + InputPacket.WireSize;
        var length = frameSize * packets.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            for (var i = 0; i < packets.Length; i++)
            {
                var offset = i * frameSize;
                buffer[offset] = 0;
                packets.Span[i].WriteTo(buffer.AsSpan(offset + 1, InputPacket.WireSize));
            }
            await link.WriteAsync(buffer.AsMemory(0, length), token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Detach(id, link);
            return false;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private async Task ReadPeerAsync(Guid id, PeerLink link)
    {
        try
        {
            var frame = new byte[1 + InputPacket.WireSize];
            while (!_stop.IsCancellationRequested)
            {
                await link.Stream.ReadExactlyAsync(frame, _stop.Token);
                link.LastReceived = Environment.TickCount64;
                if (_trust.Find(id)?.PublicKey != link.PublicKey) break;
                if (frame[0] == 0 && InputPacket.TryDecode(frame.AsSpan(1), out var packet))
                    PacketReceived?.Invoke(id, packet);
                else if (frame[0] == 1)
                {
                    var pong = new byte[frame.Length]; pong[0] = 2;
                    await link.WriteAsync(pong, _stop.Token);
                }
                else if (frame[0] != 2) break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { Detach(id, link); }
    }

    private async Task HeartbeatAsync(Guid id, PeerLink link)
    {
        try
        {
            while (IsCurrent(id, link) && !_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token);
                if (Environment.TickCount64 - link.LastReceived > 8000) break;
                var ping = new byte[1 + InputPacket.WireSize]; ping[0] = 1;
                await link.WriteAsync(ping, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { Detach(id, link); }
    }

    private bool IsCurrent(Guid id, PeerLink link) => _links.TryGetValue(id, out var current) && ReferenceEquals(current, link);
    private void Detach(Guid id, PeerLink link)
    {
        if (!_links.TryRemove(new KeyValuePair<Guid, PeerLink>(id, link))) return;
        link.Dispose();
        PeerChanged?.Invoke(id, false);
    }

    private async Task AnnounceLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Announcement("DuoTrack-LAN-1", _identity.DeviceId, ListenPort));
                foreach (var network in LocalNetworks())
                {
                    try
                    {
                        var address = network.Address.GetAddressBytes();
                        var mask = network.Mask.GetAddressBytes();
                        var broadcast = new IPAddress(address.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray());
                        await _discovery!.SendAsync(bytes, new IPEndPoint(broadcast, DiscoveryPort), _stop.Token);
                    }
                    catch (SocketException) { }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task DiscoverLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await _discovery!.ReceiveAsync(_stop.Token);
                if (message.Buffer.Length > 512 || !OnLocalSubnet(message.RemoteEndPoint.Address)) continue;
                try
                {
                    var hint = JsonSerializer.Deserialize<Announcement>(message.Buffer);
                    if (hint is null || hint.Protocol != "DuoTrack-LAN-1" || hint.Port is < 1 or > 65535
                        || !_trust.Contains(hint.Id) || hint.Id == _identity.DeviceId) continue;
                    // Only one side dials; this avoids simultaneous duplicate links.
                    if (_identity.DeviceId.CompareTo(hint.Id) < 0 && _dialing.Count < 4)
                        Track(ConnectPeerAsync(hint.Id, new IPEndPoint(message.RemoteEndPoint.Address, hint.Port)));
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private static bool OnLocalSubnet(IPAddress remote)
    {
        var bytes = remote.GetAddressBytes();
        return bytes.Length == 4 && LocalNetworks().Any(network =>
            network.Address.GetAddressBytes().Zip(network.Mask.GetAddressBytes(), (a, mask) => (byte)(a & mask))
                .SequenceEqual(bytes.Zip(network.Mask.GetAddressBytes(), (a, mask) => (byte)(a & mask))));
    }

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalNetworks()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null)
            .Select(a => (a.Address, a.IPv4Mask)).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.Stop();
        _discovery?.Dispose();
        foreach (var pair in _links) Detach(pair.Key, pair.Value);
        // Do not block the WPF thread; TLS owns a separate private-key handle.
        _ = Task.WhenAll(_tasks.Keys).ContinueWith(completed =>
        {
            _ = completed.Exception;
            _certificate.Dispose();
        }, TaskScheduler.Default);
    }

    private sealed record Announcement(string Protocol, Guid Id, int Port);

    private sealed class PeerLink(TcpClient client, SslStream stream, string publicKey) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public SslStream Stream { get; } = stream;
        public string PublicKey { get; } = publicKey;
        public long LastReceived = Environment.TickCount64;
        public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await _sendLock.WaitAsync(timeout.Token);
            try { await Stream.WriteAsync(frame, timeout.Token); }
            finally { _sendLock.Release(); }
        }
        public void Dispose() { client.Dispose(); Stream.Dispose(); }
    }
}
