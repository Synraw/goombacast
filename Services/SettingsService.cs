using System;
using System.IO;
using System.Text.Json;

namespace GoombaCast.Services
{
    public class AppSettings
    {
        public string? SelectedMicrophoneId { get; set; }
        public int VolumeLevel { get; set; } = 0;
        public string? InputDeviceId { get; set; }
    }

    public sealed class SettingsService
    {
        private static readonly Lazy<SettingsService> _default = new(() => new SettingsService());
        public static SettingsService Default => _default.Value;

        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public AppSettings Settings { get; private set; } = new();

        private SettingsService()
        {
            _filePath = GetDefaultPath();
            Load();
        }

        private static string GetDefaultPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoombaCast");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                    if (loaded is not null)
                        Settings = loaded;
                }
            }
            catch
            {
                // Optionally log
            }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Optionally log
            }
        }
    }
}