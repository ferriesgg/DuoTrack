namespace DuoTrack.Client;

internal sealed record ChallengeResponse(string Challenge);
internal sealed record SessionResponse(string Token, Guid DeviceId, string DeviceName);
internal sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);
internal sealed record DeviceInfo(Guid Id, string Name, bool Online)
{
    public string DisplayText => $"{(Online ? "●" : "○")}  {Name}  {(Online ? "在线" : "离线")}";
}
