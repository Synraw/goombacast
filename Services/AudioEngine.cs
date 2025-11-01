using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Models.Audio.Streaming;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GoombaCast.Services
{
    public sealed class AudioEngine : IDisposable
    {
        public enum AudioStreamType
        {
            Microphone,
            Loopback
        }

        private readonly IcecastStream _icecastStream;
        private readonly LevelMeterAudioHandler _levelMeter;
        private readonly GainAudioHandler _gain;
        private readonly LimiterAudioHandler _limiter;
        private readonly AudioRecorderHandler _recorder;
        
        private IAudioStream? _audioStream;
        private AudioStreamType _streamType;
        private readonly object _streamLock = new();

        public event Action<float, float>? LevelsAvailable;
        public event Action<bool>? ClippingDetected;

        public AudioEngine(SynchronizationContext? uiContext)
        {
            var settings = SettingsService.Default.Settings;

            ArgumentNullException.ThrowIfNull(settings.ServerAddress, nameof(settings.ServerAddress));

            try
            {
                Uri serverUri = new(settings.ServerAddress);
            }
            catch (UriFormatException)
            {
                Logging.LogWarning($"Invalid server address in settings: {settings.ServerAddress}");
                settings.ServerAddress = string.Empty;
            }

            _icecastStream = new IcecastStream();

            _levelMeter = new LevelMeterAudioHandler
            {
                CallbackContext = uiContext,
                UseRmsLevels = true,
                LevelFloorDb = -90f
            };

            _levelMeter.LevelsAvailable += (l, r) =>
            {
                LevelsAvailable?.Invoke(l, r);
            };

            _levelMeter.ClippingDetected += (isClipping) =>
            {
                ClippingDetected?.Invoke(isClipping);
            };

            _gain = new GainAudioHandler
            {
                GainDb = settings.VolumeLevel
            };

            _limiter = new LimiterAudioHandler
            {
                Enabled = settings.LimiterEnabled,
                ThresholdDb = settings.LimiterThreshold
            };

            _recorder = new AudioRecorderHandler();
        }

        public void SetGainLevel(float gainDb)
            => _gain.GainDb = gainDb;

        public float GetGainLevel()
            => _gain.GainDb;

        public bool IsBroadcasting => _icecastStream.IsOpen;
        public bool IsRecording => _recorder.IsRecording;
        public AudioStreamType CurrentStreamType => _streamType;

        public static InputDevice? FindInputDevice(string deviceId)
            => InputDevice.GetActiveInputDevices().Find(d => d.Id == deviceId);

        public static OutputDevice? FindOutputDevice(string deviceId)
            => OutputDevice.GetActiveOutputDevices().Find(d => d.Id == deviceId);

        public void RecreateAudioStream(AudioStreamType streamType)
        {
            if(streamType == _streamType && _audioStream != null) //dont need to re-create the underlying stream engine, just the device
                return;

            lock (_streamLock)
            {
                try
                {
                    if (_audioStream != null)
                    {
                        _audioStream.Stop();
                        _audioStream.Dispose();
                        _audioStream = null;
                    }

                    var settings = SettingsService.Default.Settings;
                    string? deviceId = streamType == AudioStreamType.Microphone
                        ? settings.MicrophoneDeviceId
                        : settings.LoopbackDeviceId;

                    _streamType = streamType;

                    // Create the appropriate stream type
                    if (_streamType == AudioStreamType.Microphone)
                    {
                        var inputDevice = FindInputDevice(deviceId ?? string.Empty);
                        _audioStream = new MicrophoneStream(_icecastStream, inputDevice);
                    }
                    else
                    {
                        var outputDevice = FindOutputDevice(deviceId ?? string.Empty);
                        _audioStream = new LoopbackStream(_icecastStream, outputDevice);
                    }

                    // Add handlers
                    _audioStream.AddAudioHandler(_levelMeter);
                    _audioStream.AddAudioHandler(_gain);
                    _audioStream.AddAudioHandler(_limiter);
                    _audioStream.AddAudioHandler(_recorder);

                    _audioStream.Start();
                }
                catch (Exception ex)
                {
                    Logging.Log($"Error recreating audio stream: {ex.Message}");
                    throw;
                }
            }
        }

        public bool ChangeDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            if (_audioStream == null) return false;

            lock (_streamLock)
            {
                try
                {
                    bool wasRunning = _audioStream.IsRunning;
                    
                    // Always stop before changing device
                    if (wasRunning)
                    {
                        _audioStream.Stop();
                    }

                    bool result = false;
                    if (_streamType == AudioStreamType.Microphone)
                    {
                        var device = FindInputDevice(deviceId);
                        if (device != null)
                        {
                            result = _audioStream.ChangeDevice(device.Id);
                        }
                    }
                    else
                    {
                        var device = FindOutputDevice(deviceId);
                        if (device != null)
                        {
                            result = _audioStream.ChangeDevice(device.Id);
                        }
                    }

                    if (wasRunning && result)
                    {
                        _audioStream.Start();
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    Logging.Log($"Error changing device: {ex.Message}");
                    return false;
                }
            }
        }

        public void Start() 
            => _audioStream?.Start();
        
        public void Stop() 
            => _audioStream?.Stop();

        public async Task StartBroadcastAsync()
        {
            if (!_icecastStream.IsOpen)
            {
                _icecastStream.Configure(IcecastStreamConfig.FromSettings(SettingsService.Default));
            }

            await _icecastStream.Connect().ConfigureAwait(false);
        }

        public async Task StopBroadcast()
        {
            await _icecastStream.Disconnect().ConfigureAwait(false);
        }

        public void StartBroadcast()
        {
            if (!_icecastStream.IsOpen)
            {
                _icecastStream.Configure(IcecastStreamConfig.FromSettings(SettingsService.Default));
            }
            _icecastStream.Connect().GetAwaiter().GetResult();
        }

        public void SetLimiterEnabled(bool enabled) 
            => _limiter.Enabled = enabled;

        public void SetLimiterThreshold(float thresholdDb) 
            => _limiter.ThresholdDb = thresholdDb;

        public void StartRecording(string directory)
        {
            _recorder.StartRecording(directory);
        }

        public void StopRecording()
        {
            _recorder.StopRecording();
        }

        public void Dispose()
        {
            _audioStream?.Dispose();
            _recorder.Dispose();
        }
    }
}