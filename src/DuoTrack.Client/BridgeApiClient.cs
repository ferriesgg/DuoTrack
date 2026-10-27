using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace DuoTrack.Client;

internal sealed class BridgeApiClient(DeviceIdentity identity, string? peerCachePath = null) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private Uri? _baseUri;

    public string? SessionToken { get; private set; }
    public TrustedPeerStore TrustedPeers { get; private set; } = null!;

    public void Configure(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("请输入有效的服务器地址，例如 https://duotrack.example.com");
        }
        if (_baseUri != uri) TrustedPeers = new TrustedPeerStore(uri.AbsoluteUri.TrimEnd('/'), peerCachePath);
        _baseUri = uri;
    }

    public async Task AuthenticateAsync(string deviceName, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await RegisterAsync(deviceName, cancellationToken);
        var challengeResponse = await _http.GetAsync(BuildUri($"/api/auth/challenge/{identity.DeviceId:D}"), cancellationToken);
        challengeResponse.EnsureSuccessStatusCode();

        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("服务器返回了无效的验证信息。");
        var request = new
        {
            deviceId = identity.DeviceId.ToString("D"),
            challenge = challenge.Challenge,
            signature = identity.Sign(Encoding.UTF8.GetBytes(challenge.Challenge)),
        };
        var response = await _http.PostAsJsonAsync(BuildUri("/api/auth/session"), request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("服务器没有返回会话凭据。");
        SessionToken = session.Token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var devices = await _http.GetFromJsonAsync<List<DeviceInfo>>(BuildUri("/api/devices"), cancellationToken) ?? [];
        TrustedPeers.Update(devices, replace: true);
        return devices;
    }

    public async Task<PairingCodeResponse> CreatePairingCodeAsync(CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var response = await _http.PostAsJsonAsync(BuildUri("/api/pairing/code"), new { }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingCodeResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("服务器没有返回配对码。");
    }

    public async Task<DeviceInfo> JoinPairingAsync(string code, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var response = await _http.PostAsJsonAsync(BuildUri("/api/pairing/join"), new { code }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("配对码无效、已过期或不能与本机配对。");
        }
        var device = await response.Content.ReadFromJsonAsync<DeviceInfo>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("服务器没有返回配对设备。");
        TrustedPeers.Update([device], replace: false);
        return device;
    }

    public Uri BuildWebSocketUri()
    {
        EnsureConfigured();
        var builder = new UriBuilder(_baseUri!)
        {
            Scheme = _baseUri!.Scheme == "https" ? "wss" : "ws",
            Port = _baseUri.IsDefaultPort ? -1 : _baseUri.Port,
            Path = "/connect",
            Query = string.Empty,
        };
        return builder.Uri;
    }

    private async Task RegisterAsync(string deviceName, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes($"{identity.DeviceId:D}\n{deviceName.Trim()}\n{identity.PublicKey}");
        var request = new
        {
            deviceId = identity.DeviceId.ToString("D"),
            name = deviceName.Trim(),
            publicKey = identity.PublicKey,
            signature = identity.Sign(payload),
        };
        var response = await _http.PostAsJsonAsync(BuildUri("/api/devices/register"), request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private Uri BuildUri(string path) => new(_baseUri!, path);
    private void EnsureConfigured()
    {
        if (_baseUri is null) throw new InvalidOperationException("尚未配置服务器地址。");
    }
    private void EnsureAuthenticated()
    {
        EnsureConfigured();
        if (SessionToken is null) throw new InvalidOperationException("客户端尚未连接服务器。");
    }
    public void Dispose() => _http.Dispose();
}
