using System.Text.Json;

namespace DuoTrack.Client;

internal static class AppStorage
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DuoTrack");

    public static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public static string IdentityPath => Path.Combine(DirectoryPath, "identity.json");

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void EnsureDirectory() => Directory.CreateDirectory(DirectoryPath);
}
