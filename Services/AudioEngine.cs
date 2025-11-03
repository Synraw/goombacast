using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Models.Audio.Streaming;
using NAudio.Lame;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        private readonly MixerOutputHandler _mixerOutput;

        // Dedicated virtual stream for mixer output
        private readonly VirtualMixerStream _mixerStream;

        // Null stream to prevent user input sources from writing to Icecast
        private readonly NullIcecastStream _nullStream = new();

        private readonly List<AudioInputSource> _inputSources = new();
        private readonly object _sourcesLock = new();

        // Clock source - only this source triggers mixing
        private Guid? _clockSourceId = null;

        // Buffer queues and timestamps for input sources
        private readonly ConcurrentDictionary<Guid, Queue<(byte[] buffer, int length, long timestamp)>> _sourceBufferQueues = new();
        private readonly ConcurrentDictionary<Guid, long> _sourceTimestamps = new();

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

            _mixerOutput = new MixerOutputHandler(_mixer);

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

            _mixerStream = new VirtualMixerStream(_icecastStream);

            SetupPostMixProcessingChain(_mixerStream);

            _mixerStream.Start();
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
                    stream = new MicrophoneStream(_nullStream, device);
                }
                else
                {
                    var device = FindOutputDevice(deviceId);
                    if (device == null)
                        throw new ArgumentException($"Output device not found: {deviceId}");

                    deviceName = device.ToString();
                    stream = new LoopbackStream(_nullStream, device);
                }

                var source = new AudioInputSource(deviceName, deviceId, streamType);
                source.SetStream(stream);

                // Add input source handler that routes to mixer and triggers processing
                stream.AddAudioHandler(new InputSourceHandler(source.Id, _mixer, this));

                _inputSources.Add(source);
                _mixer.AddInputSource(source);

                // Set the first source as the clock source
                if (_clockSourceId == null)
                {
                    _clockSourceId = source.Id;
                    Logging.Log($"Set clock source to: {deviceName} ({source.Id})");
                }

                SyncInputSourcesToSettings();

                stream.Start();

                Logging.Log($"Added input source: {deviceName}");
                return source;
            }
        }

        /// <summary>
        /// Called by input sources when they have data ready - queues data and triggers mixer processing
        /// </summary>
        internal void NotifySourceReady(Guid sourceId, int bufferSize)
        {
            // Initialize queue if needed
            if (!_sourceBufferQueues.ContainsKey(sourceId))
            {
                _sourceBufferQueues[sourceId] = new Queue<(byte[] buffer, int length, long timestamp)>();
            }

            var timestamp = DateTime.UtcNow.Ticks;
            _sourceTimestamps[sourceId] = timestamp;

            // Only trigger mixing from the clock source
            if (sourceId == _clockSourceId)
            {
                _mixerStream.TriggerMixerProcessing(bufferSize);
            }
        }

        /// <summary>
        /// Sets up the processing chain that runs AFTER mixing on the dedicated mixer stream
        /// </summary>
        private void SetupPostMixProcessingChain(VirtualMixerStream mixerStream)
        {
            // Add handlers in processing order:
            // 1. Mixer output (gets mixed audio)
            // 2. Level meter (measure mixed levels)
            // 3. Master gain (apply master volume)
            // 4. Limiter (prevent clipping)
            // 5. Recorder (record final output)

            mixerStream.AddAudioHandler(_mixerOutput);
            mixerStream.AddAudioHandler(_levelMeter);
            mixerStream.AddAudioHandler(_masterGain);
            mixerStream.AddAudioHandler(_limiter);
            mixerStream.AddAudioHandler(_recorder);
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

                    // If we're removing the clock source, reassign to the first remaining source
                    if (_clockSourceId == source.Id)
                    {
                        _clockSourceId = _inputSources.FirstOrDefault()?.Id;
                        if (_clockSourceId.HasValue)
                        {
                            var newClockSource = _inputSources.First(s => s.Id == _clockSourceId.Value);
                            Logging.Log($"Reassigned clock source to: {newClockSource.Name} ({_clockSourceId})");
                        }
                        else
                        {
                            Logging.Log("No clock source remaining");
                        }
                    }

                    source.Dispose();

                    SyncInputSourcesToSettings();

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
                        newStream = new MicrophoneStream(_nullStream, device);
                    }
                    else
                    {
                        var device = FindOutputDevice(newDeviceId);
                        if (device == null) return false;
                        deviceName = device.ToString();
                        newStream = new LoopbackStream(_nullStream, device);
                    }

                    // Update source
                    source.DeviceId = newDeviceId;
                    source.Name = deviceName;
                    source.SetStream(newStream);

                    // Add input routing handler
                    newStream.AddAudioHandler(new InputSourceHandler(source.Id, _mixer, this));

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
            _mixerStream?.Stop();
            _mixerStream?.Dispose();
            _recorder.Dispose();
            _nullStream.Dispose();
        }

        /// <summary>
        /// Routes individual input stream buffers to the mixer and notifies when ready
        /// </summary>
        private class InputSourceHandler : IAudioHandler
        {
            private readonly Guid _sourceId;
            private readonly AudioMixerHandler _mixer;
            private readonly AudioEngine _engine;

            public InputSourceHandler(Guid sourceId, AudioMixerHandler mixer, AudioEngine engine)
            {
                _sourceId = sourceId;
                _mixer = mixer;
                _engine = engine;
            }

            public string FriendlyName => "Input Source Router";
            public int Order => -100;
            public bool Enabled => true;

            public void OnStart(WaveFormat waveFormat) { }
            public void OnStop() { }

            public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
            {
                // Send audio data to mixer
                _mixer.ProcessInputBuffer(_sourceId, buffer, offset, count);

                // Notify engine that this source has provided data - trigger mixing
                _engine.NotifySourceReady(_sourceId, count);
            }
        }

        /// <summary>
        /// Retrieves mixed audio from the mixer and replaces the buffer
        /// </summary>
        private class MixerOutputHandler : IAudioHandler
        {
            private readonly AudioMixerHandler _mixer;

            public MixerOutputHandler(AudioMixerHandler mixer)
            {
                _mixer = mixer;
            }

            public string FriendlyName => "Mixer Output";
            public int Order => -50;
            public bool Enabled => true;

            public void OnStart(WaveFormat waveFormat)
            {
                _mixer.OnStart(waveFormat);
            }

            public void OnStop()
            {
                _mixer.OnStop();
            }

            public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
            {
                // Get mixed output and replace the buffer
                _mixer.ProcessBuffer(buffer, offset, count, waveFormat);
            }
        }

        /// <summary>
        /// Virtual stream dedicated to mixer output processing
        /// Triggered by input sources when they provide data
        /// </summary>
        private class VirtualMixerStream : IAudioStream
        {
            private readonly IcecastStream _icecastStream;
            private readonly WaveFormat _waveFormat;
            private readonly List<IAudioHandler> _handlers = new();
            private readonly object _handlerLock = new();
            private readonly object _processLock = new();
            private LameMP3FileWriter? _mp3Writer;
            private bool _running;
            private bool _disposed;

            public WaveFormat WaveFormat => _waveFormat;
            public bool IsRunning => _running;

            public VirtualMixerStream(IcecastStream icecastStream)
            {
                _icecastStream = icecastStream;
                _waveFormat = new WaveFormat(48000, 16, 2); // Standard stereo 48kHz
            }

            public void Start()
            {
                if (_running) return;

                _mp3Writer = new LameMP3FileWriter(_icecastStream, _waveFormat, 320);
                _running = true;

                // Notify handlers
                lock (_handlerLock)
                {
                    foreach (var handler in _handlers)
                    {
                        handler.OnStart(_waveFormat);
                    }
                }

                Logging.Log("Virtual mixer stream started");
            }

            public void Stop()
            {
                if (!_running) return;

                _running = false;

                // Notify handlers
                lock (_handlerLock)
                {
                    foreach (var handler in _handlers)
                    {
                        handler.OnStop();
                    }
                }

                _mp3Writer?.Dispose();
                _mp3Writer = null;

                Logging.Log("Virtual mixer stream stopped");
            }

            /// <summary>
            /// Called when an input source provides data - processes the mix immediately
            /// </summary>
            public void TriggerMixerProcessing(int bufferSize)
            {
                if (!_running) return;

                lock (_processLock)
                {
                    try
                    {
                        // Create buffer for mixed audio
                        byte[] buffer = new byte[bufferSize];

                        // Process through handler chain
                        lock (_handlerLock)
                        {
                            foreach (var handler in _handlers.OrderBy(h => h.Order))
                            {
                                if (handler.Enabled)
                                {
                                    handler.ProcessBuffer(buffer, 0, bufferSize, _waveFormat);
                                }
                            }
                        }

                        // Write to MP3 encoder and Icecast
                        _mp3Writer?.Write(buffer, 0, bufferSize);
                    }
                    catch (Exception ex)
                    {
                        Logging.LogError($"Error in virtual mixer stream: {ex.Message}");
                    }
                }
            }

            public void AddAudioHandler(IAudioHandler handler)
            {
                lock (_handlerLock)
                {
                    if (!_handlers.Contains(handler))
                    {
                        _handlers.Add(handler);

                        if (_running)
                        {
                            handler.OnStart(_waveFormat);
                        }
                    }
                }
            }

            public bool RemoveAudioHandler(IAudioHandler handler)
            {
                lock (_handlerLock)
                {
                    bool removed = _handlers.Remove(handler);
                    if (removed && _running)
                    {
                        handler.OnStop();
                    }
                    return removed;
                }
            }

            public bool ChangeDevice(string? deviceId)
            {
                // Virtual stream doesn't have a physical device
                return false;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                Stop();
            }
        }

        /// <summary>
        /// Null stream that discards all writes (used for user input sources)
        /// </summary>
        private class NullIcecastStream : IcecastStream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get; set; }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { } // Discard
        }

        /// <summary>
        /// Synchronizes current input sources to settings
        /// </summary>
        private void SyncInputSourcesToSettings()
        {
            var settings = SettingsService.Default.Settings;
            settings.InputSources.Clear();

            foreach (var source in _inputSources)
            {
                settings.InputSources.Add(new AppSettings.InputSourceConfig
                {
                    DeviceId = source.DeviceId,
                    StreamType = source.StreamType,
                    Volume = source.Volume,
                    IsMuted = source.IsMuted,
                    IsSolo = source.IsSolo
                });
            }

            SettingsService.Default.Save();
        }
    }
}