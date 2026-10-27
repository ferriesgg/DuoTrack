using System.Text.Json;

namespace DuoTrack.Client;

internal sealed class ClientSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;
    public bool StartWithWindows { get; set; }
    public bool EdgeSwitchEnabled { get; set; }
    public bool RelayScrollSmoothing { get; set; } = true;
    public DuoTrack.Protocol.ScreenSide RemoteScreenSide { get; set; } = DuoTrack.Protocol.ScreenSide.Right;
    public Guid? LayoutDeviceId { get; set; }

    public static ClientSettings Load()
    {
        AppStorage.EnsureDirectory();
        try
        {
            return File.Exists(AppStorage.SettingsPath)
                ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(AppStorage.SettingsPath), AppStorage.JsonOptions)
                    ?? new ClientSettings()
                : new ClientSettings();
        }
        catch
        {
            return new ClientSettings();
        }
    }

    public void Save()
    {
        AppStorage.EnsureDirectory();
        File.WriteAllText(AppStorage.SettingsPath, JsonSerializer.Serialize(this, AppStorage.JsonOptions));
    }
}
