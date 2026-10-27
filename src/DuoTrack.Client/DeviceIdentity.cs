using System.Security.Cryptography;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;

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

    public string Sign(byte[] payload)
    {
        lock (_key) return Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256));
    }

    public X509Certificate2 CreateLanCertificate()
    {
        lock (_key)
        {
            // A separate key handle keeps TLS independent of API authentication.
            using var key = ECDsa.Create();
            var privateBytes = _key.ExportPkcs8PrivateKey();
            try { key.ImportPkcs8PrivateKey(privateBytes, out _); }
            finally { CryptographicOperations.ZeroMemory(privateBytes); }
            var request = new CertificateRequest($"CN=DuoTrack-{DeviceId:D}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
            // Windows Schannel cannot use CreateSelfSigned's ephemeral key handle
            // for TLS. Import a temporary user-key container (deleted on dispose).
            var pfx = generated.Export(X509ContentType.Pfx);
            try { return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }

    internal static DeviceIdentity CreateEphemeral() => new(Guid.NewGuid(), ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public void Dispose() => _key.Dispose();

    private sealed record StoredIdentity(Guid DeviceId, string ProtectedPrivateKey);
}
