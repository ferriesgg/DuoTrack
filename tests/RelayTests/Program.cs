using DuoTrack.Protocol;
using DuoTrack.Relay;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

var first = Guid.NewGuid(); var second = Guid.NewGuid();
var inputs = Enumerable.Range(0, 64).Select(i => new InputPacket(InputEventType.MouseMove, i, -i)).ToArray();
var batch = RelayWire.EncodeBatch(second, inputs);
if (!RelayWire.TryDecodeBatch(batch, out var target, out var decoded) || target != second || !decoded.SequenceEqual(inputs))
    throw new Exception("Batch roundtrip or input ordering failed");
if (RelayWire.TryDecodeBatch(batch.AsSpan(0, batch.Length - 1), out _, out _)) throw new Exception("Truncated batch accepted");
var invalid = batch.ToArray(); invalid[17] = 65;
if (RelayWire.TryDecodeBatch(invalid, out _, out _)) throw new Exception("Oversized batch accepted");
invalid = batch.ToArray(); invalid[19] = 255;
if (RelayWire.TryDecodeBatch(invalid, out _, out _)) throw new Exception("Invalid event in batch accepted");
if (!RelayWire.TryDecodePing(RelayWire.EncodePing(12345), out var stamp) || stamp != 12345) throw new Exception("Relay RTT frame failed");
Console.WriteLine("PASS: relay batch format, 64-event ordering, invalid length/type rejection and RTT framing");

var queries = 0;
using var cache = new PairAuthorizationCache((a, b) => { queries++; return Task.FromResult(a == first && b == second); });
for (var i = 0; i < 10000; i++)
    if (!await cache.IsAllowedAsync(first, second)) throw new Exception("Paired devices denied");
if (queries != 1 || !await cache.IsAllowedAsync(second, first)) throw new Exception("Permission cache still queries each input");
if (await cache.IsAllowedAsync(first, Guid.NewGuid())) throw new Exception("Unpaired devices allowed");
Console.WriteLine("PASS: 10,000 paired input checks use one database lookup; unpaired target denied");

var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var stale = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var calls = 0;
using var racing = new PairAuthorizationCache(async (_, _) =>
{
    if (++calls == 1) { started.SetResult(); return await stale.Task; }
    return true;
});
var pending = racing.IsAllowedAsync(first, second).AsTask();
await started.Task;
racing.Paired(first, second);
stale.SetResult(false);
if (!await pending || calls != 2) throw new Exception("Stale permission result overwrote a new pairing");
Console.WriteLine("PASS: concurrent pairing invalidates stale permission lookups immediately");

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
await using var legacyApp = builder.Build();
legacyApp.Urls.Add("http://127.0.0.1:0");
legacyApp.UseWebSockets();
legacyApp.Map("/legacy", async context =>
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync(); // Older relays select no subprotocol.
    var bytes = new byte[128];
    var result = await socket.ReceiveAsync(bytes, context.RequestAborted);
    if (result.MessageType == WebSocketMessageType.Close)
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
});
await legacyApp.StartAsync();
using (var client = new ClientWebSocket())
{
    client.Options.AddSubProtocol(RelayWire.BatchSubProtocol);
    await client.ConnectAsync(new Uri(legacyApp.Urls.Single().Replace("http:", "ws:") + "/legacy"), CancellationToken.None);
    if (!string.IsNullOrEmpty(client.SubProtocol)) throw new Exception("Legacy server negotiated an unexpected protocol");
    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
}
await legacyApp.StopAsync();
Console.WriteLine("PASS: a new client can connect to a legacy relay which does not select a subprotocol");
