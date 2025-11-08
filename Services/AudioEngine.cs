using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Models.Audio.Streaming;
using NAudio.Lame;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GoombaCast.Services
{
    /// <summary>
    /// Core audio engine responsible for managing input sources, mixing, and output streaming
    /// </summary>
    public sealed class AudioEngine : IAsyncDisposable
    {
        public enum AudioStreamType
        {
            Microphone,
            Loopback
        }

        // ============================================================================
        // Fields
        // ============================================================================

        // Core streaming and processing components
        private readonly IcecastStream _icecastStream;
        private readonly AudioMixerHandler _mixer;
        private readonly VirtualMixerStream _mixerStream;

        // Audio processing handlers (in order of processing)
        private LevelMeterAudioHandler? _levelMeter;
        private GainAudioHandler? _masterGain;
        private LimiterAudioHandler? _limiter;
        private AudioRecorderHandler? _recorder;

        // Disposal flag
        private const int NotDisposed = 0;
        private const int Disposed = 1;
        private int _disposeState = 0; // 0 = not disposed, 1 = disposing/disposed
        
        // Null stream to discard individual source output
        private readonly NullIcecastStream _nullStream = new();

        // Input source management
        private readonly List<AudioInputSource> _inputSources = new();
        private readonly object _sourcesLock = new();
        private Guid? _clockSourceId = null; // First source drives the mixer timing

        // ============================================================================
        // Events and Properties
        // ============================================================================

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

        public bool IsBroadcasting => _icecastStream.IsOpen;
        public bool IsRecording => _recorder?.IsRecording ?? false;

        // ============================================================================
        // Constructor and Initialization
        // ============================================================================

        public AudioEngine(SynchronizationContext? uiContext)
        {
            ValidateSettings();

            _icecastStream = new IcecastStream();
            _mixer = new AudioMixerHandler();
            _mixerStream = new VirtualMixerStream(_icecastStream);

            InitializeAudioHandlers(uiContext);
            SetupProcessingChain();

            _mixerStream.Start();
        }

        private static void ValidateSettings()
        {
            var settings = SettingsService.Default.Settings;
            ArgumentNullException.ThrowIfNull(settings.ServerAddress, nameof(settings.ServerAddress));

            if (!Uri.TryCreate(settings.ServerAddress, UriKind.Absolute, out _))
            {
                Logging.LogWarning($"Invalid server address in settings: {settings.ServerAddress}");
                settings.ServerAddress = string.Empty;
            }
        }

        private void InitializeAudioHandlers(SynchronizationContext? uiContext)
        {
            var settings = SettingsService.Default.Settings;

            _levelMeter = new LevelMeterAudioHandler
            {
                CallbackContext = uiContext,
                UseRmsLevels = true,
                LevelFloorDb = -90f
            };

            _levelMeter.LevelsAvailable += (l, r) => LevelsAvailable?.Invoke(l, r);
            _levelMeter.ClippingDetected += (isClipping) => ClippingDetected?.Invoke(isClipping);

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

        private void SetupProcessingChain()
        {
            // Processing order:
            // 1. Mixer output (gets mixed audio)
            // 2. Level meter (measure mixed levels)
            // 3. Master gain (apply master volume)
            // 4. Limiter (prevent clipping)
            // 5. Recorder (record final output)
            _mixerStream.AddAudioHandler(new MixerOutputHandler(_mixer));
            _mixerStream.AddAudioHandler(_levelMeter!);
            _mixerStream.AddAudioHandler(_masterGain!);
            _mixerStream.AddAudioHandler(_limiter!);
            _mixerStream.AddAudioHandler(_recorder!);
        }

        // ============================================================================
        // Audio Controls
        // ============================================================================

        public void SetMasterGainLevel(float gainDb)
            => _masterGain!.GainDb = gainDb;

        public float GetMasterGainLevel()
            => _masterGain!.GainDb;

        public void SetMasterVolume(float volume)
            => _mixer.MasterVolume = volume;

        public float GetMasterVolume()
            => _mixer.MasterVolume;

        public void SetLimiterEnabled(bool enabled)
            => _limiter!.Enabled = enabled;

        public void SetLimiterThreshold(float thresholdDb)
            => _limiter!.ThresholdDb = thresholdDb;

        // ============================================================================
        // Broadcasting
        // ============================================================================

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

        // ============================================================================
        // Recording
        // ============================================================================

        public void StartRecording(string directory)
        {
            _recorder?.StartRecording(directory);
        }

        public void StopRecording()
        {
            _recorder?.StopRecording();
        }

        // ============================================================================
        // Input Source Management
        // ============================================================================

        /// <summary>
        /// Adds a new input source to the mixer
        /// </summary>
        public AudioInputSource AddInputSource(string deviceId, AudioStreamType streamType)
        {
            lock (_sourcesLock)
            {
                var (deviceName, stream) = CreateAudioStream(deviceId, streamType);

                var source = new AudioInputSource(deviceName, deviceId, streamType);
                source.SetStream(stream);

                // Route this source's audio to the mixer 
                stream.AddAudioHandler(new InputSourceHandler(source.Id, _mixer, this, source));

                _inputSources.Add(source);
                _mixer.AddInputSource(source);

                // First source becomes the clock source (drives mixer timing)
                _clockSourceId ??= source.Id;

                SyncInputSourcesToSettings();
                stream.Start();

                Logging.Log($"Added source: {deviceName}");
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
                if (!_inputSources.Remove(source)) return;
                
                RemoveInputSourceInternal(source);
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
                if (source != null && _inputSources.Remove(source))
                {
                    RemoveInputSourceInternal(source);
                }
            }
        }

        /// <summary>
        /// Internal method that removes a source (must be called with lock held)
        /// </summary>
        private void RemoveInputSourceInternal(AudioInputSource source)
        {
            _mixer.RemoveInputSource(source);

            // Reassign clock source if needed
            if (_clockSourceId == source.Id)
            {
                _clockSourceId = _inputSources.FirstOrDefault()?.Id;
            }

            // Only sync if not disposing
            if (_disposeState == NotDisposed)
            {
                SyncInputSourcesToSettings();
            }

            Logging.Log($"Removed source: {source.Name}");
            
            Task.Run(() =>
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Error disposing source {source.Name}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Clears all input sources
        /// </summary>
        public void ClearInputSources()
        {
            List<AudioInputSource> sourcesToDispose;
            
            lock (_sourcesLock)
            {
                sourcesToDispose = _inputSources.ToList();
                _inputSources.Clear();
                _mixer.ClearInputSources(); // Assuming this method exists
                _clockSourceId = null;
            }
            
            // Dispose all sources OUTSIDE the lock
            foreach (var source in sourcesToDispose)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Error disposing source {source.Name}: {ex.Message}");
                }
            }
        }

        // ============================================================================
        // Device Lookup
        // ============================================================================

        public static InputDevice? FindInputDevice(string deviceId)
            => InputDevice.GetActiveInputDevices().Find(d => d.Id == deviceId);

        public static OutputDevice? FindOutputDevice(string deviceId)
            => OutputDevice.GetActiveOutputDevices().Find(d => d.Id == deviceId);

        // ============================================================================
        // Private Helpers
        // ============================================================================

        private (string deviceName, IAudioStream stream) CreateAudioStream(string deviceId, AudioStreamType streamType)
        {
            if (streamType == AudioStreamType.Microphone)
            {
                var device = FindInputDevice(deviceId)
                ?? throw new ArgumentException($"Input device not found: {deviceId}");
                return (device.ToString(), new MicrophoneStream(_nullStream, device));
            }
            else
            {
                var device = FindOutputDevice(deviceId)
                      ?? throw new ArgumentException($"Output device not found: {deviceId}");
                return (device.ToString(), new LoopbackStream(_nullStream, device));
            }
        }

        /// <summary>
        /// Called by input sources when they provide audio data - triggers mixer processing
        /// </summary>
        internal void NotifySourceReady(Guid sourceId, int bufferSize)
        {
            // Only the clock source triggers mixing (prevents duplicate processing)
            if (sourceId == _clockSourceId)
            {
                _mixerStream.TriggerMixerProcessing(bufferSize);
            }
        }

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

        // ============================================================================
        // Disposal
        // ============================================================================

        public async ValueTask DisposeAsync()
        {
            // Ensure disposal only happens once
            if (Interlocked.Exchange(ref _disposeState, Disposed) != NotDisposed)
                return;

            // Stop mixer stream first
            _mixerStream?.Stop();

            // Clear and dispose all input sources
            ClearInputSources();

            // Dispose audio handlers
            if (_recorder != null)
            {
                await Task.Run(() => _recorder.Dispose()).ConfigureAwait(false);
            }

            // Dispose mixer components
            _mixerStream?.Dispose();
            _nullStream?.Dispose();

            // Dispose Icecast stream if open
            if (_icecastStream.IsOpen)
            {
                await _icecastStream.DisposeAsync().ConfigureAwait(false);
            }

            GC.SuppressFinalize(this);
        }

        // ============================================================================
        // Nested Classes - Audio Handlers and Streams
        // ============================================================================

        /// <summary>
        /// Routes individual input stream buffers to the mixer
        /// </summary>
        private sealed class InputSourceHandler : IAudioHandler
        {
            private readonly Guid _sourceId;
            private readonly AudioMixerHandler _mixer;
            private readonly AudioEngine _engine;
            private readonly AudioInputSource _source;

            public string FriendlyName => "Input Router";
            public int Order => -100;
            public bool Enabled => true;

            public InputSourceHandler(Guid sourceId, AudioMixerHandler mixer, AudioEngine engine, AudioInputSource source)
            {
                _sourceId = sourceId;
                _mixer = mixer;
                _engine = engine;
                _source = source;
            }

            public void OnStart(WaveFormat waveFormat) { }
            public void OnStop() { }

            public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
            {
                bool shouldProcess = ShouldProcessSource();

                if (shouldProcess)
                {
                    _mixer.ProcessInputBuffer(_sourceId, buffer, offset, count);
                }
                else
                {
                    // Feed silence to keep the buffer in sync
                    byte[] silence = new byte[count];
                    Array.Clear(silence, 0, count);
                    _mixer.ProcessInputBuffer(_sourceId, silence, 0, count);
                }

                _engine.NotifySourceReady(_sourceId, count);
            }

            private bool ShouldProcessSource()
            {
                if (_source.IsMuted) return false;

                var allSources = _engine.InputSources; // Returns a copy, thread-safe
                bool hasSolo = allSources.Any(s => s.IsSolo);
                if (hasSolo) return _source.IsSolo;
                
                return true;
            }
        }

        /// <summary>
        /// Retrieves mixed audio from the mixer
        /// </summary>
        private sealed class MixerOutputHandler(AudioMixerHandler mixer) : IAudioHandler
        {
            private readonly AudioMixerHandler _mixer = mixer;

            public string FriendlyName => "Mixer Output";
            public int Order => -50;
            public bool Enabled => true;

            public void OnStart(WaveFormat waveFormat) => _mixer.OnStart(waveFormat);
            public void OnStop() => _mixer.OnStop();
            public void ProcessBuffer(byte[] buffer, int offset, int count, WaveFormat waveFormat)
                => _mixer.ProcessBuffer(buffer, offset, count, waveFormat);
        }

        /// <summary>
        /// Virtual stream for mixer output processing - triggered by clock source
        /// </summary>
        private sealed class VirtualMixerStream(IcecastStream icecastStream) : IAudioStream
        {
            private readonly IcecastStream _icecastStream = icecastStream;
            private readonly WaveFormat _waveFormat = new(48000, 16, 2);
            private readonly List<IAudioHandler> _handlers = new();
            private readonly object _handlerLock = new();
            private readonly object _processLock = new();

            private LameMP3FileWriter? _mp3Writer;
            private bool _running;
            private bool _disposed;

            public WaveFormat WaveFormat => _waveFormat;
            public bool IsRunning => _running;

            public void Start()
            {
                if (_running) return;

                _mp3Writer = new LameMP3FileWriter(_icecastStream, _waveFormat, 320);
                _running = true;

                lock (_handlerLock)
                {
                    foreach (var handler in _handlers)
                    {
                        handler.OnStart(_waveFormat);
                    }
                }
            }

            public void Stop()
            {
                if (!_running) return;

                _running = false;

                lock (_handlerLock)
                {
                    foreach (var handler in _handlers)
                    {
                        handler.OnStop();
                    }
                }

                _mp3Writer?.Dispose();
                _mp3Writer = null;
            }

            public void TriggerMixerProcessing(int bufferSize)
            {
                if (!_running) return;

                lock (_processLock)
                {
                    try
                    {
                        byte[] buffer = new byte[bufferSize];

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

                        _mp3Writer?.Write(buffer, 0, bufferSize);
                    }
                    catch (Exception ex)
                    {
                        Logging.LogError($"Error in mixer stream: {ex.Message}");
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
                        if (_running) handler.OnStart(_waveFormat);
                    }
                }
            }

            public bool RemoveAudioHandler(IAudioHandler handler)
            {
                lock (_handlerLock)
                {
                    bool removed = _handlers.Remove(handler);
                    if (removed && _running) handler.OnStop();
                    return removed;
                }
            }

            public bool ChangeDevice(string? deviceId) => false;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Stop();
            }
        }

        /// <summary>
        /// Null stream that discards all writes (used for individual input sources)
        /// </summary>
        private sealed class NullIcecastStream : IcecastStream
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
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
