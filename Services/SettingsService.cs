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
        [JsonPropertyName("hideLog")]
        public bool HideLog { get; set; } = false;
       
        [JsonPropertyName("volumeLevel")]
        public float VolumeLevel { get; set; } = 0;
        
        [JsonPropertyName("currentServer")]
        public ServerProfileConfig? CurrentServer { get; set; }

        [JsonPropertyName("limiterEnabled")]
        public bool LimiterEnabled { get; set; } = true;
       
        [JsonPropertyName("limiterThreshold")]
        public float LimiterThreshold { get; set; } = -3.0f;
       
        [JsonPropertyName("audioBufferMs")]
        public int AudioBufferMs { get; set; } = 20; // Default 20ms (low latency)
       
        [JsonPropertyName("conversionBufferMs")]
        public int ConversionBufferMs { get; set; } = 100; // Default 100ms
       
        [JsonPropertyName("recordingDirectory")]
        public string RecordingDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "GoombaCast Recordings"
        );
        
        [JsonPropertyName("serverProfiles")]
        public List<ServerProfileConfig> ServerProfiles { get; set; } = new();
       
        [JsonPropertyName("inputSources")]
        public List<InputSourceConfig> InputSources { get; set; } = new();

        public bool IsValid()
        {
            return CurrentServer != null;
        }

        public class InputSourceConfig
        {
            [JsonPropertyName("deviceId")]
            public string DeviceId { get; set; } = string.Empty;
            [JsonPropertyName("streamType")]
            public AudioEngine.AudioStreamType StreamType { get; set; }
            [JsonPropertyName("volume")]
            public float Volume { get; set; } = 1.0f;
            [JsonPropertyName("isMuted")]
            public bool IsMuted { get; set; }
            [JsonPropertyName("isSolo")]
            public bool IsSolo { get; set; }
        }

        public class ServerProfileConfig
        {
            [JsonPropertyName("profileName")]
            public string ProfileName { get; set; } = string.Empty;
            [JsonPropertyName("serverAddress")]
            public string ServerAddress { get; set; } = string.Empty;
            [JsonPropertyName("userName")]
            public string UserName { get; set; } = string.Empty;
            [JsonPropertyName("password")]
            public string Password { get; set; } = string.Empty;

            public override string ToString()
            {
                return ProfileName;
            }

            public Uri? ServerURI()
            {
                if(Uri.TryCreate(ServerAddress, UriKind.Absolute, out Uri? uri))
                    return uri;
                return null;
            }
        }

        public bool IsServerAddressValid(ServerProfileConfig serverProfile)
           => Uri.TryCreate(serverProfile.ServerAddress, UriKind.Absolute, out var uri) &&
              (uri.Scheme == "http" || uri.Scheme == "https");

        public bool IsServerAddressValid()
           => CurrentServer != null && IsServerAddressValid(CurrentServer);
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
            _filePath = GetLocalPath();
            _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            LoadSync();
        }

        private static string GetLocalPath()
        {
            var dir = Directory.GetCurrentDirectory();
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