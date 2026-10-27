namespace DuoTrack.Client;

internal sealed record ChallengeResponse(string Challenge);
internal sealed record SessionResponse(string Token, Guid DeviceId, string DeviceName);
internal sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);
internal sealed record DeviceInfo(Guid Id, string Name, bool Online, string? PublicKey = null, string? ConnectionMode = null)
{
    public string DisplayText => $"{(Online ? "●" : "○")}  {Name}  {(Online ? ConnectionMode ?? "在线" : "离线")}";
}
