using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using DuoTrack.Protocol;
using DuoTrack.Relay;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddFixedWindowLimiter("pairing", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});
var dataDirectory = Environment.GetEnvironmentVariable("DUOTRACK_DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);

var repository = new DeviceRepository(Path.Combine(dataDirectory, "duotrack.db"));
repository.Initialize();
var sessions = new SessionStore();
var challenges = new ConcurrentDictionary<Guid, PendingChallenge>();
var connections = new ConnectionRegistry();

var app = builder.Build();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/devices/register", async (RegisterDeviceRequest request) =>
{
    if (!Guid.TryParse(request.DeviceId, out var deviceId) ||
        string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 ||
        string.IsNullOrWhiteSpace(request.PublicKey) || string.IsNullOrWhiteSpace(request.Signature))
    {
        return Results.BadRequest(new { error = "invalid-registration" });
    }

    try
    {
        var name = request.Name.Trim();
        var canonical = DeviceCrypto.RegistrationPayload(deviceId, name, request.PublicKey);
        if (!DeviceCrypto.Verify(request.PublicKey, canonical, request.Signature))
        {
            return Results.Unauthorized();
        }

        var result = await repository.RegisterDeviceAsync(deviceId, name, request.PublicKey);
        return result switch
        {
            RegistrationResult.Created => Results.Ok(new { deviceId }),
            RegistrationResult.AlreadyExists => Results.Ok(new { deviceId }),
            _ => Results.Conflict(new { error = "device-id-already-uses-another-key" }),
        };
    }
    catch (Exception ex) when (ex is CryptographicException or FormatException)
    {
        return Results.BadRequest(new { error = "invalid-public-key-or-signature" });
    }
});

app.MapGet("/api/auth/challenge/{deviceId:guid}", async (Guid deviceId) =>
{
    if (!await repository.DeviceExistsAsync(deviceId))
    {
        return Results.NotFound(new { error = "device-not-registered" });
    }

    var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    challenges[deviceId] = new PendingChallenge(challenge, DateTimeOffset.UtcNow.AddMinutes(2));
    return Results.Ok(new ChallengeResponse(challenge));
});

app.MapPost("/api/auth/session", async (CreateSessionRequest request) =>
{
    if (!Guid.TryParse(request.DeviceId, out var deviceId) ||
        !challenges.TryRemove(deviceId, out var pending) ||
        pending.ExpiresAt <= DateTimeOffset.UtcNow || pending.Value != request.Challenge)
    {
        return Results.Unauthorized();
    }

    var device = await repository.GetDeviceAsync(deviceId);
    if (device is null || !DeviceCrypto.Verify(
            device.PublicKey,
            Encoding.UTF8.GetBytes(request.Challenge),
            request.Signature))
    {
        return Results.Unauthorized();
    }

    var token = sessions.Create(deviceId);
    return Results.Ok(new SessionResponse(token, deviceId, device.Name));
});

app.MapGet("/api/devices", async (HttpContext context) =>
{
    if (!sessions.TryAuthenticate(context.Request, out var deviceId))
    {
        return Results.Unauthorized();
    }

    var devices = await repository.GetPairedDevicesAsync(deviceId);
    return Results.Ok(devices.Select(device =>
        new DeviceResponse(device.Id, device.Name, connections.IsOnline(device.Id), device.PublicKey)));
});

app.MapPost("/api/pairing/code", async (HttpContext context) =>
{
    if (!sessions.TryAuthenticate(context.Request, out var deviceId))
    {
        return Results.Unauthorized();
    }

    var code = await repository.CreatePairingCodeAsync(deviceId);
    return Results.Ok(new PairingCodeResponse(code, DateTimeOffset.UtcNow.AddMinutes(5)));
});

app.MapPost("/api/pairing/join", async (HttpContext context, JoinPairingRequest request) =>
{
    if (!sessions.TryAuthenticate(context.Request, out var deviceId))
    {
        return Results.Unauthorized();
    }

    var pairedDevice = await repository.JoinPairingAsync(deviceId, request.Code.Replace(" ", string.Empty));
    return pairedDevice is null
        ? Results.BadRequest(new { error = "invalid-or-expired-code" })
        : Results.Ok(new DeviceResponse(pairedDevice.Id, pairedDevice.Name, connections.IsOnline(pairedDevice.Id), pairedDevice.PublicKey));
}).RequireRateLimiting("pairing");

app.Map("/connect", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest ||
        !sessions.TryAuthenticate(context.Request, out var deviceId))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var endpoint = new RelayEndpoint(deviceId, socket);
    if (!connections.TryAttach(endpoint))
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "This device is already connected", context.RequestAborted);
        return;
    }

    app.Logger.LogInformation("Device {DeviceId} connected", deviceId);
    try
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType != WebSocketMessageType.Binary || !result.EndOfMessage ||
                !RelayWire.TryDecodeInput(buffer.AsSpan(0, result.Count), out var targetId, out var packet))
            {
                await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid input message", context.RequestAborted);
                break;
            }

            if (!await repository.ArePairedAsync(deviceId, targetId))
            {
                continue;
            }

            if (connections.TryGet(targetId, out var target))
            {
                // Client -> relay carries the destination; relay -> client carries
                // the authenticated source so a screen can return control to it.
                await target.SendAsync(RelayWire.EncodeInput(deviceId, packet), context.RequestAborted);
            }
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (WebSocketException ex)
    {
        app.Logger.LogInformation("Device {DeviceId} disconnected: {Message}", deviceId, ex.Message);
    }
    finally
    {
        connections.Detach(endpoint);
        var release = new InputPacket(InputEventType.ReleaseAll);
        foreach (var peer in await repository.GetPairedDevicesAsync(deviceId))
        {
            if (connections.TryGet(peer.Id, out var target))
            {
                try
                {
                    await target.SendAsync(RelayWire.EncodeInput(deviceId, release), CancellationToken.None);
                }
                catch
                {
                    // The peer may be disconnecting at the same time.
                }
            }
        }
        app.Logger.LogInformation("Device {DeviceId} left", deviceId);
    }
});

app.Run();

record RegisterDeviceRequest(string DeviceId, string Name, string PublicKey, string Signature);
record ChallengeResponse(string Challenge);
record CreateSessionRequest(string DeviceId, string Challenge, string Signature);
record SessionResponse(string Token, Guid DeviceId, string DeviceName);
record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);
record JoinPairingRequest(string Code);
record DeviceResponse(Guid Id, string Name, bool Online, string PublicKey);
record PendingChallenge(string Value, DateTimeOffset ExpiresAt);

static class DeviceCrypto
{
    public static byte[] RegistrationPayload(Guid id, string name, string publicKey) =>
        Encoding.UTF8.GetBytes($"{id:D}\n{name}\n{publicKey}");

    public static bool Verify(string publicKey, byte[] payload, string signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return ecdsa.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
    }
}

sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new(StringComparer.Ordinal);

    public string Create(Guid deviceId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new DeviceSession(deviceId, DateTimeOffset.UtcNow.AddHours(12));
        return token;
    }

    public bool TryAuthenticate(HttpRequest request, out Guid deviceId)
    {
        deviceId = default;
        const string prefix = "Bearer ";
        var value = request.Headers.Authorization.ToString();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = value[prefix.Length..];
        if (!_sessions.TryGetValue(token, out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        deviceId = session.DeviceId;
        return true;
    }

    private sealed record DeviceSession(Guid DeviceId, DateTimeOffset ExpiresAt);
}

sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, RelayEndpoint> _connections = new();
    public bool TryAttach(RelayEndpoint endpoint) => _connections.TryAdd(endpoint.DeviceId, endpoint);
    public bool TryGet(Guid id, out RelayEndpoint endpoint) => _connections.TryGetValue(id, out endpoint!);
    public bool IsOnline(Guid id) => _connections.ContainsKey(id);
    public void Detach(RelayEndpoint endpoint) =>
        _connections.TryRemove(new KeyValuePair<Guid, RelayEndpoint>(endpoint.DeviceId, endpoint));
}

sealed class RelayEndpoint(Guid deviceId, WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public Guid DeviceId { get; } = deviceId;

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
