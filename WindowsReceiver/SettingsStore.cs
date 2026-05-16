using System.IO;
using System.Text.Json;

namespace PhoneShareReceiver;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PhoneShareReceiver"
    );

    public static string SettingsPath => Path.Combine(AppDir, "settings.json");

    public static ReceiverSettings Load()
    {
        Directory.CreateDirectory(AppDir);
        if (!File.Exists(SettingsPath))
        {
            var fresh = new ReceiverSettings();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<ReceiverSettings>(json, Options) ?? new ReceiverSettings();
            if (string.IsNullOrWhiteSpace(settings.DeviceId)) settings.DeviceId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(settings.DeviceName)) settings.DeviceName = Environment.MachineName;
            if (string.IsNullOrWhiteSpace(settings.Token)) settings.Token = SecurityUtil.CreateToken();
            if (string.IsNullOrWhiteSpace(settings.SaveFolder))
                settings.SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PhoneShare");
            if (settings.Port <= 0) settings.Port = 53318;
            if (settings.MaxFileSizeMb <= 0) settings.MaxFileSizeMb = 4096;
            settings.PairedPhones ??= new List<PairedPhoneDevice>();
            settings.PairedPhones = settings.PairedPhones
                .Where(p => !string.IsNullOrWhiteSpace(p.DeviceId))
                .GroupBy(p => p.DeviceId)
                .Select(g => g.OrderByDescending(p => p.LastPairedAt).First())
                .OrderByDescending(p => p.LastPairedAt)
                .ToList();
            return settings;
        }
        catch
        {
            var fresh = new ReceiverSettings();
            Save(fresh);
            return fresh;
        }
    }

    public static void Save(ReceiverSettings settings)
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}
