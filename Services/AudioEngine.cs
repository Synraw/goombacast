using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Models.Audio.Streaming;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GoombaCast.Services
{
    public sealed class AudioEngine : IDisposable
    {
        private readonly IcecastStream _icecastStream;
        private readonly MicrophoneStream _micStream;
        private readonly LevelMeterAudioHandler _levelMeter;
        private readonly GainAudioHandler _gain;
        private readonly LimiterAudioHandler _limiter;

        public void SetGainLevel(double gainDb) 
            => _gain.GainDb = gainDb;

        public double GetGainLevel() 
            => _gain.GainDb;

        // Re-expose levels for view models (already marshalled to UI thread)
        public event Action<float, float>? LevelsAvailable;
        
        // Re-expose clipping detection for view models (already marshalled to UI thread)
        public event Action<bool>? ClippingDetected;

        public static InputDevice? FindInputDevice(string deviceId)
            => InputDevice.GetActiveInputDevices().Find(d => d.Id == deviceId);

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
                // Ensure callbacks fire on the UI thread
                CallbackContext = uiContext,
                UseRmsLevels = true,
                LevelFloorDb = -90f
            };

            _levelMeter.LevelsAvailable += (l, r) => LevelsAvailable?.Invoke(l, r);

            _levelMeter.ClippingDetected += (isClipping) => ClippingDetected?.Invoke(isClipping);

            _gain = new GainAudioHandler
            {
                GainDb = settings.VolumeLevel
            };

            _limiter = new LimiterAudioHandler
            {
                Enabled = settings.LimiterEnabled,
                ThresholdDb = settings.LimiterThreshold
            };

            var inputDeviceId = SettingsService.Default.Settings.InputDeviceId;
            InputDevice? device = null;
            if (inputDeviceId is not null)
            {
                device = FindInputDevice(inputDeviceId);
            }
            
            _micStream = new MicrophoneStream(_icecastStream, device);
            _micStream.AddAudioHandler(_gain);
            _micStream.AddAudioHandler(_levelMeter);
            _micStream.AddAudioHandler(_limiter);
        }

        public bool ChangeInputDevice(string deviceId)
        {
            var device = FindInputDevice(deviceId);
            if (device is not null)
            {
                _micStream.SetInputDevice(device);
                return true;
            }
            return false;
        }

        public void Start()
        {
            _micStream.Start();
        }

        public void Stop() => _micStream.Stop();

        public bool IsBroadcasting => _icecastStream.IsOpen;

        public async Task StartBroadcastAsync(CancellationToken ct = default)
        {
            if (!_icecastStream.IsOpen)
            {
                _icecastStream.Configure(IcecastStreamConfig.FromSettings(SettingsService.Default));
            }

            await _icecastStream.OpenAsync(ct).ConfigureAwait(false);
        }

        public void StopBroadcast()
        {
            _icecastStream.Disconnect();
        }

        // Legacy sync method kept for completeness (unused by UI)
        public void StartBroadcast()
        {
            if (!_icecastStream.IsOpen)
            {
                _icecastStream.Configure(IcecastStreamConfig.FromSettings(SettingsService.Default));
            }
            _icecastStream.Connect();
        }

        public void SetLimiterEnabled(bool enabled) 
            => _limiter.Enabled = enabled;

        public void SetLimiterThreshold(float thresholdDb) 
            => _limiter.ThresholdDb = thresholdDb;

        public void Dispose() => _micStream.Dispose();
    }
}