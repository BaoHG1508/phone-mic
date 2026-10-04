using System.Text.Json;

namespace PhoneMicReceiver;

/// <summary>Persisted in %AppData%\PhoneMic\settings.json.</summary>
public sealed class Settings
{
    public string? DeviceId { get; set; }
    public int Port { get; set; } = PhoneMic.Core.Pmic.DefaultPort;
    public int BufferMs { get; set; } = 40;
    public int MaxBufferMs { get; set; } = 250;
    public bool AutoStart { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string? Name { get; set; }

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneMic");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch
        {
            // corrupt file: fall back to defaults
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // not fatal
        }
    }
}
