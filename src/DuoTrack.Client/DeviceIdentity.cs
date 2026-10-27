using System.Security.Cryptography;
using System.Text.Json;

namespace DuoTrack.Client;

internal sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    private DeviceIdentity(Guid deviceId, ECDsa key)
    {
        DeviceId = deviceId;
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public Guid DeviceId { get; }
    public string PublicKey { get; }

    public static DeviceIdentity LoadOrCreate()
    {
        AppStorage.EnsureDirectory();
        if (File.Exists(AppStorage.IdentityPath))
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(
                File.ReadAllText(AppStorage.IdentityPath), AppStorage.JsonOptions)
                ?? throw new InvalidDataException("设备身份文件已损坏。");
            var protectedBytes = Convert.FromBase64String(stored.ProtectedPrivateKey);
            var privateBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateBytes, out _);
            CryptographicOperations.ZeroMemory(privateBytes);
            return new DeviceIdentity(stored.DeviceId, key);
        }

        var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = newKey.ExportPkcs8PrivateKey();
        var protectedKey = ProtectedData.Protect(privateKey, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(privateKey);
        var identity = new StoredIdentity(Guid.NewGuid(), Convert.ToBase64String(protectedKey));
        File.WriteAllText(AppStorage.IdentityPath, JsonSerializer.Serialize(identity, AppStorage.JsonOptions));
        return new DeviceIdentity(identity.DeviceId, newKey);
    }

    public string Sign(byte[] payload) =>
        Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256));

    public void Dispose() => _key.Dispose();

    private sealed record StoredIdentity(Guid DeviceId, string ProtectedPrivateKey);
}
