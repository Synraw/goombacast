using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GoombaCast.Services
{
    public class AppSettings
    {
        [JsonPropertyName("volumeLevel")]
        public int VolumeLevel { get; set; }
        [JsonPropertyName("serverAddress")]
        public string ServerAddress { get; set; } = "http://localhost:8005/";
        [JsonPropertyName("streamName")]
        public string StreamName { get; set; } = "My Stream";
        [JsonPropertyName("userName")]
        public string UserName { get; set; } = "source";
        [JsonPropertyName("password")]
        public string Password { get; set; } = "hackme";
        [JsonPropertyName("limiterEnabled")]
        public bool LimiterEnabled { get; set; } = true;
        [JsonPropertyName("limiterThreshold")]
        public float LimiterThreshold { get; set; } = -3.0f;
        [JsonPropertyName("recordingDirectory")]
        public string RecordingDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "GoombaCast Recordings"
        );
        [JsonPropertyName("microphoneDeviceId")]
        public string? MicrophoneDeviceId { get; set; }
        [JsonPropertyName("loopbackDeviceId")]
        public string? LoopbackDeviceId { get; set; }
        [JsonPropertyName("audioStreamType")]
        public AudioEngine.AudioStreamType AudioStreamType { get; set; } = AudioEngine.AudioStreamType.Microphone;
        [JsonPropertyName("inputSources")]
        public List<InputSourceConfig> InputSources { get; set; } = new();

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(ServerAddress) &&
                   !string.IsNullOrWhiteSpace(UserName) &&
                   !string.IsNullOrWhiteSpace(Password);
        }

        public bool IsServerAddressValid()
            => Uri.TryCreate(ServerAddress, UriKind.Absolute, out var uri) &&
               (uri.Scheme == "http" || uri.Scheme == "https");


        public class InputSourceConfig
        {
            [JsonPropertyName("deviceId")]
            public string DeviceId { get; set; } = string.Empty;

            [JsonPropertyName("streamType")]
            public AudioEngine.AudioStreamType StreamType { get; set; }

            [JsonPropertyName("volume")]
            public float Volume { get; set; } = 1.0f;
        }
    }

    public sealed class SettingsService : IDisposable
    {
        private static readonly Lazy<SettingsService> _default = new(() => new SettingsService());
        public static SettingsService Default => _default.Value;

        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _isDisposed;

        // Can hook into this to receive updates on our settings changes (save/load atm)
        public event EventHandler<AppSettings>? SettingsChanged;

        public AppSettings Settings { get; private set; } = new();

        private SettingsService()
        {
            _filePath = GetDefaultPath();
            _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            LoadSync();
        }

        private static string GetDefaultPath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appDataPath, "GoombaCast");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        private void LoadSync()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    Logging.Log("Settings file not found, using defaults");
                    return;
                }

                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                if (loaded is not null)
                {
                    Settings = loaded;
                    ValidateSettings();
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to load settings: {ex.Message}");
            }
        }

        public async Task LoadAsync()
        {
            ThrowIfDisposed();

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(_filePath))
                {
                    Logging.Log("Settings file not found, using defaults");
                    return;
                }

                var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                if (loaded is not null)
                {
                    Settings = loaded;
                    ValidateSettings();
                    SettingsChanged?.Invoke(this, Settings);
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to load settings: {ex.Message}");
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveAsync()
        {
            ThrowIfDisposed();

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                ValidateSettings();
                var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                var tempPath = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
                File.Move(tempPath, _filePath, true);
                SettingsChanged?.Invoke(this, Settings);
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to save settings: {ex.Message}");
                throw;
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Save()
        {
            ThrowIfDisposed();
            try
            {
                ValidateSettings();
                var json = JsonSerializer.Serialize(Settings, _jsonOptions);
                var tempPath = Path.GetTempFileName();
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, true);
                SettingsChanged?.Invoke(this, Settings);
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to save settings: {ex.Message}");
                throw;
            }
        }

        private void ValidateSettings()
        {
            if (!Settings.IsValid())
            {
                Logging.LogWarning("Settings validation failed");
            }

            if (!Settings.IsServerAddressValid())
            {
                Logging.LogWarning($"Invalid server address: {Settings.ServerAddress}");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, nameof(SettingsService));
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _lock.Dispose();
        }
    }
}