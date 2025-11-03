using CommunityToolkit.Mvvm.ComponentModel;
using GoombaCast.Services;
using NAudio.Wave;
using System;

namespace GoombaCast.Models.Audio.Streaming
{
    /// <summary>
    /// Represents a single audio input source with individual volume control
    /// </summary>
    public partial class AudioInputSource : ObservableObject, IDisposable
    {
        private IAudioStream? _stream;
        private bool _disposed;

        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _deviceId = string.Empty;
        [ObservableProperty] private float _volume = 1.0f; // Linear 0.0 to 1.0
        [ObservableProperty] private bool _isMuted;
        [ObservableProperty] private bool _isSolo;
        [ObservableProperty] private AudioEngine.AudioStreamType _streamType;

        public Guid Id { get; } = Guid.NewGuid();
        public IAudioStream? Stream => _stream;

        public AudioInputSource(string name, string deviceId, AudioEngine.AudioStreamType streamType)
        {
            Name = name;
            DeviceId = deviceId;
            StreamType = streamType;
        }

        public void SetStream(IAudioStream stream)
        {
            _stream = stream;
        }

        partial void OnVolumeChanged(float value)
        {
            SyncToSettings();
        }

        partial void OnIsMutedChanged(bool value)
        {
            SyncToSettings();
        }

        partial void OnIsSoloChanged(bool value)
        {
            SyncToSettings();
        }

        private void SyncToSettings()
        {
            if (_disposed) return;

            try
            {
                var settings = SettingsService.Default.Settings;
                var config = settings.InputSources.Find(s => s.DeviceId == DeviceId);
                
                if (config != null)
                {
                    config.Volume = Volume;
                    // Note: mute/solo states are typically session-only,
                    // but you can add them to InputSourceConfig if you want persistence
                    SettingsService.Default.Save();
                }
            }
            catch (Exception ex)
            {
                Logging.LogError($"Failed to sync input source to settings: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _stream?.Dispose();
            _disposed = true;
        }
    }
}