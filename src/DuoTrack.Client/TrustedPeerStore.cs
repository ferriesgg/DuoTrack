using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DuoTrack.Client;

internal sealed record TrustedPeer(Guid Id, string Name, string PublicKey);

internal sealed class TrustedPeerStore
{
    private readonly ConcurrentDictionary<Guid, TrustedPeer> _peers = new();
    private readonly string _path;
    private readonly object _saveLock = new();

    public TrustedPeerStore(string serverScope, string? path = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverScope)));
        _path = path ?? Path.Combine(AppStorage.DirectoryPath, $"peers-{hash[..16]}.json");
        try
        {
            if (File.Exists(_path))
                foreach (var peer in JsonSerializer.Deserialize<List<TrustedPeer>>(File.ReadAllText(_path)) ?? [])
                    if (ValidKey(peer.PublicKey)) _peers[peer.Id] = peer;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<TrustedPeer> All => _peers.Values.ToArray();
    public bool Contains(Guid id) => _peers.ContainsKey(id);
    public TrustedPeer? Find(Guid id) => _peers.GetValueOrDefault(id);

    // Only call with authenticated relay responses, never with discovery data.
    public void Update(IEnumerable<DeviceInfo> devices, bool replace)
    {
        var verified = devices.Where(d => d.PublicKey is not null && ValidKey(d.PublicKey)).ToArray();
        lock (_saveLock)
        {
            var changed = false;
            // An older relay has no public-key field. Preserve the existing cache.
            if (replace && verified.Length == devices.Count())
                foreach (var id in _peers.Keys.Except(verified.Select(d => d.Id))) changed |= _peers.TryRemove(id, out _);
            foreach (var device in verified)
            {
                var peer = new TrustedPeer(device.Id, device.Name, device.PublicKey!);
                if (_peers.TryGetValue(device.Id, out var existing) && existing == peer) continue;
                _peers[device.Id] = peer;
                changed = true;
            }
            if (!changed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(All));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    public Guid? Authenticate(X509Certificate? certificate, Guid? expectedPeer = null)
    {
        if (certificate is null) return null;
        try
        {
            using var cert = new X509Certificate2(certificate);
            using var key = cert.GetECDsaPublicKey();
            if (key is null) return null;
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            foreach (var peer in _peers.Values)
                if ((expectedPeer is null || expectedPeer == peer.Id) && peer.PublicKey == publicKey)
                    return peer.Id;
        }
        catch (CryptographicException) { }
        return null;
    }

    private static bool ValidKey(string key)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            return ecdsa.KeySize == 256;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }
}
