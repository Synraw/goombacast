using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Models.Audio.Streaming;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly GainAudioHandler _masterGain;
        private readonly LimiterAudioHandler _limiter;
        private readonly AudioRecorderHandler _recorder;
        private readonly AudioMixerHandler _mixer;
        
        private readonly List<AudioInputSource> _inputSources = new();
        private readonly object _sourcesLock = new();

        public event Action<float, float>? LevelsAvailable;
        public event Action<bool>? ClippingDetected;

        public IReadOnlyList<AudioInputSource> InputSources
        {
            get
            {
                lock (_sourcesLock)
                {
                    return _inputSources.ToList();
                }
            }
        }

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

            // Initialize mixer first
            _mixer = new AudioMixerHandler();

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

            _masterGain = new GainAudioHandler
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

        public void SetMasterGainLevel(float gainDb)
            => _masterGain.GainDb = gainDb;

        public float GetMasterGainLevel()
            => _masterGain.GainDb;

        public void SetMasterVolume(float volume)
            => _mixer.MasterVolume = volume;

        public float GetMasterVolume()
            => _mixer.MasterVolume;

        public bool IsBroadcasting => _icecastStream.IsOpen;
        public bool IsRecording => _recorder.IsRecording;

        public static InputDevice? FindInputDevice(string deviceId)
            => InputDevice.GetActiveInputDevices().Find(d => d.Id == deviceId);

        public static OutputDevice? FindOutputDevice(string deviceId)
            => OutputDevice.GetActiveOutputDevices().Find(d => d.Id == deviceId);

        /// <summary>
        /// Adds a new input source to the mixer
        /// </summary>
        public AudioInputSource AddInputSource(string deviceId, AudioStreamType streamType)
        {
            lock (_sourcesLock)
            {
                string deviceName;
                IAudioStream stream;

                if (streamType == AudioStreamType.Microphone)
                {
                    var device = FindInputDevice(deviceId);
                    if (device == null)
                        throw new ArgumentException($"Input device not found: {deviceId}");
                    
                    deviceName = device.ToString();
                    stream = new MicrophoneStream(_icecastStream, device);
                }
                else
                {
                    var device = FindOutputDevice(deviceId);
                    if (device == null)
                        throw new ArgumentException($"Output device not found: {deviceId}");
                    
                    deviceName = device.ToString();
                    stream = new LoopbackStream(_icecastStream, device);
                }

                var source = new AudioInputSource(deviceName, deviceId, streamType);
                source.SetStream(stream);

                // Add handlers to the stream
                stream.AddAudioHandler(new InputSourceHandler(source.Id, _mixer));
                stream.AddAudioHandler(_levelMeter);
                stream.AddAudioHandler(_masterGain);
                stream.AddAudioHandler(_limiter);
                stream.AddAudioHandler(_recorder);

                _inputSources.Add(source);
                _mixer.AddInputSource(source);

                stream.Start();

                Logging.Log($"Added input source: {deviceName}");
                return source;
            }
        }

        /// <summary>
        /// Removes an input source from the mixer
        /// </summary>
        public void RemoveInputSource(AudioInputSource source)
        {
            lock (_sourcesLock)
            {
                if (_inputSources.Remove(source))
                {
                    _mixer.RemoveInputSource(source);
                    source.Dispose();
                    Logging.Log($"Removed input source: {source.Name}");
                }
            }
        }

        /// <summary>
        /// Removes an input source by ID
        /// </summary>
        public void RemoveInputSource(Guid sourceId)
        {
            lock (_sourcesLock)
            {
                var source = _inputSources.FirstOrDefault(s => s.Id == sourceId);
                if (source != null)
                {
                    RemoveInputSource(source);
                }
            }
        }

        /// <summary>
        /// Changes the device for an existing input source
        /// </summary>
        public bool ChangeInputSourceDevice(Guid sourceId, string newDeviceId)
        {
            lock (_sourcesLock)
            {
                var source = _inputSources.FirstOrDefault(s => s.Id == sourceId);
                if (source == null) return false;

                try
                {
                    // Stop and dispose old stream
                    source.Stream?.Stop();
                    source.Stream?.Dispose();

                    // Create new stream with new device
                    IAudioStream newStream;
                    string deviceName;

                    if (source.StreamType == AudioStreamType.Microphone)
                    {
                        var device = FindInputDevice(newDeviceId);
                        if (device == null) return false;
                        deviceName = device.ToString();
                        newStream = new MicrophoneStream(_icecastStream, device);
                    }
                    else
                    {
                        var device = FindOutputDevice(newDeviceId);
                        if (device == null) return false;
                        deviceName = device.ToString();
                        newStream = new LoopbackStream(_icecastStream, device);
                    }

                    // Update source
                    source.DeviceId = newDeviceId;
                    source.Name = deviceName;
                    source.SetStream(newStream);

                    // Re-add handlers
                    newStream.AddAudioHandler(new InputSourceHandler(source.Id, _mixer));
                    newStream.AddAudioHandler(_levelMeter);
                    newStream.AddAudioHandler(_masterGain);
                    newStream.AddAudioHandler(_limiter);
                    newStream.AddAudioHandler(_recorder);

                    // Start new stream
                    newStream.Start();

                    Logging.Log($"Changed device for {source.Name} to {deviceName}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Failed to change device: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Clears all input sources
        /// </summary>
        public void ClearInputSources()
        {
            lock (_sourcesLock)
            {
                foreach (var source in _inputSources.ToList())
                {
                    RemoveInputSource(source);
                }
            }
        }

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
            ClearInputSources();
            _recorder.Dispose();
        }

        /// <summary>
        /// Helper handler that routes input to the mixer
        /// </summary>
        private class InputSourceHandler : IAudioHandler
        {
            private readonly Guid _sourceId;
            private readonly AudioMixerHandler _mixer;

            public InputSourceHandler(Guid sourceId, AudioMixerHandler mixer)
            {
                _sourceId = sourceId;
                _mixer = mixer;
            }

            public string FriendlyName => "Input Source Router";
            public int Order => -2; // Before mixer
            public bool Enabled => true;

            public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
            {
                _mixer.ProcessInputBuffer(_sourceId, buffer, offset, count);
            }
        }
    }
}