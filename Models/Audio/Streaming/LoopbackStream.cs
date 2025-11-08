using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Models.Audio.AudioProcessing;
using GoombaCast.Services;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GoombaCast.Models.Audio.Streaming
{
    public sealed class OutputDevice(MMDevice output)
    {
        public MMDevice Device { get; } = output;
        public string Id => Device.ID;
        public override string ToString() => Device.FriendlyName;

        public static List<OutputDevice> GetActiveOutputDevices()
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            return devices.Select(d => new OutputDevice(d)).ToList();
        }

        public static OutputDevice GetDefaultOutputDevice()
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new OutputDevice(device);
        }
    }

    public sealed class LoopbackStream(IcecastStream iceStream, OutputDevice? device) : IAudioStream
    {
        private WasapiLoopbackCapture? _loopback;
        private LameMP3FileWriter? _mp3Writer;
        private OutputDevice? _outputDevice = device ?? OutputDevice.GetDefaultOutputDevice();
        private IcecastStream _iceStream = iceStream;
        private volatile bool _running = false;
        private volatile bool _deviceSwitchInProgress;
        private Random? _ditherRng;

        // Fixed output format to match mixer (48kHz, 16-bit, stereo)
        private readonly WaveFormat _outputFormat = new(AudioResampler.TargetSampleRate, 16, 2);

        private readonly object _loopbackLock = new();
        private readonly object _handlerLock = new();
        private IAudioHandler[] _handlerSnapshot = [];

        // Make buffers persistent and reuse them
        private byte[]? _conversionBuffer = new byte[19200]; // Pre-allocate for 100ms @ 48kHz
        private byte[]? _resampleBuffer = new byte[19200];
        private EventHandler<WaveInEventArgs>? _dataAvailableHandler;
        private EventHandler<StoppedEventArgs>? _recordingStoppedHandler;

        public OutputDevice? CurrentOutputDevice => _outputDevice;
        public WaveFormat? WaveFormat => _outputFormat; // Always return 48kHz format
        public bool IsRunning => _running;

        private void CreateAndStartCapture(bool notifyHandlers)
        {
            if (_loopback != null)
            {
                CleanupCapture();
            }

            _loopback = new WasapiLoopbackCapture(_outputDevice?.Device);
            _mp3Writer = new LameMP3FileWriter(_iceStream, _outputFormat, 320);

            InitializeBuffers();

            if (notifyHandlers)
            {
                NotifyHandlersStart();
            }

            AttachEventHandlers();
        }

        private void InitializeBuffers()
        {
            var settings = SettingsService.Default.Settings;
            var sourceFormat = _loopback!.WaveFormat;

            // Calculate conversion buffer size
            int conversionBufferSize = CalculateBufferSize(
                sourceFormat.AverageBytesPerSecond,
                settings.ConversionBufferMs);
            _conversionBuffer = new byte[conversionBufferSize];

            // Calculate target buffer size for processing
            int targetBufferMs = settings.AudioBufferMs;
            int targetSamplesPerChannel = (_outputFormat.SampleRate * targetBufferMs) / 1000;
            int alignedBufferSize = targetSamplesPerChannel * 4; // 4 bytes for stereo 16-bit

            // Ensure conversion buffer is large enough
            int requiredSize = CalculateBufferSize(
                sourceFormat.AverageBytesPerSecond,
                targetBufferMs);

            if (_conversionBuffer.Length < requiredSize * 2)
            {
                _conversionBuffer = new byte[requiredSize * 2];
            }

            _resampleBuffer = new byte[alignedBufferSize * 2];
        }

        private static int CalculateBufferSize(int bytesPerSecond, int milliseconds)
        {
            int size = (bytesPerSecond * milliseconds) / 1000;
            return (size + 3) & ~3; // Align to 4-byte boundary
        }

        private void NotifyHandlersStart()
        {
            var handlers = _handlerSnapshot;
            foreach (var handler in handlers)
            {
                handler.OnStart(_outputFormat);
            }
        }

        private void AttachEventHandlers()
        {
            var loopbackRef = _loopback!;
            var sourceChannels = _loopback!.WaveFormat.Channels;
            var sourceSampleRate = _loopback!.WaveFormat.SampleRate;

            _dataAvailableHandler = (s, a) => ProcessAudioData(
                a,
                loopbackRef,
                sourceChannels,
                sourceSampleRate);

            _recordingStoppedHandler = (s, a) => HandleRecordingStopped(loopbackRef);

            _loopback.DataAvailable += _dataAvailableHandler;
            _loopback.RecordingStopped += _recordingStoppedHandler;
        }

        private void ProcessAudioData(WaveInEventArgs args, WasapiLoopbackCapture loopbackRef, int sourceChannels, int sourceSampleRate)
        {
            if (loopbackRef != _loopback || !_running) return;

            try
            {
                // Step 1: Convert to stereo Int16
                var (outputBuffer, outputLength) = ConvertAndResample(
                    args.Buffer,
                    args.BytesRecorded,
                    sourceChannels,
                    sourceSampleRate);

                // Check again before processing handlers (double-check pattern)
                if (!_running) return;

                var handlers = _handlerSnapshot;

                // Step 2: Send to handlers
                ProcessHandlers(handlers, outputBuffer, outputLength);

                // Step 3: Write to MP3
                WriteToMp3(outputBuffer, outputLength, loopbackRef);
            }   
            catch (Exception ex)
            {
                Logging.LogError($"Error processing loopback audio: {ex.Message}");
            }
        }

        private (byte[] buffer, int length) ConvertAndResample(
            byte[] sourceBuffer,
            int bytesRecorded,
            int sourceChannels,
            int sourceSampleRate)
        {
            // Convert float32 to int16 stereo
            int stereoBytes = (bytesRecorded / sourceChannels) * 2;
            if (_conversionBuffer!.Length < stereoBytes)
            {
                Array.Resize(ref _conversionBuffer, stereoBytes);
            }

            int convertedLength = AudioFormatConverter.ConvertFloat32ToStereoInt16(
                sourceBuffer,
                _conversionBuffer,
                bytesRecorded,
                sourceChannels,
                _ditherRng ??= new Random());

            // Resample if needed
            if (sourceSampleRate != _outputFormat.SampleRate)
            {
                int outputLength = AudioResampler.ResampleTo48kHz(
                    _conversionBuffer,
                    convertedLength,
                    sourceSampleRate,
                    _resampleBuffer!,
                    ref _resampleBuffer!);

                return (_resampleBuffer!, outputLength);
            }

            return (_conversionBuffer, convertedLength);
        }

        private void ProcessHandlers(IAudioHandler[] handlers, byte[] buffer, int length)
        {
            for (int i = 0; i < handlers.Length; i++)
            {
                var handler = handlers[i];
                if (handler.Enabled)
                {
                    handler.ProcessBuffer(buffer, 0, length, _outputFormat);
                }
            }
        }

        private void WriteToMp3(byte[] buffer, int length, WasapiLoopbackCapture loopbackRef)
        {
            if (!_running || loopbackRef != _loopback) return;
            
            var writer = _mp3Writer;
            if (writer != null)
            {
                try
                {
                    writer.Write(buffer, 0, length);
                }
                catch (ObjectDisposedException) { /*Can safely ignore*/ }
            }
        }

        private void HandleRecordingStopped(WasapiLoopbackCapture loopbackRef)
        {
            if (_deviceSwitchInProgress) return;

            if (loopbackRef == _loopback)
            {
                Stop();
            }
        }

        private void CleanupCapture()
        {
            if (_loopback != null)
            {
                if (_dataAvailableHandler != null)
                {
                    _loopback.DataAvailable -= _dataAvailableHandler;
                    _dataAvailableHandler = null;
                }

                if (_recordingStoppedHandler != null)
                {
                    _loopback.RecordingStopped -= _recordingStoppedHandler;
                    _recordingStoppedHandler = null;
                }

                try
                {
                    _loopback.StopRecording();
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Error stopping loopback during cleanup: {ex.Message}");
                }

                _loopback.Dispose();
                _loopback = null;
            }

            _conversionBuffer = null;
            _resampleBuffer = null;
        }

        public bool ChangeDevice(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            var device = OutputDevice.GetActiveOutputDevices().FirstOrDefault(d =>
                string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device == null) return false;
            return SetOutputDevice(device);
        }

        private bool SetOutputDevice(OutputDevice? device)
        {
            if (device == null) return false;

            if (string.Equals(_outputDevice?.Id, device.Id, StringComparison.OrdinalIgnoreCase))
                return true;

            _deviceSwitchInProgress = true;
            _outputDevice = device;
            RestartCapture();
            Logging.Log($"Selected output device {_outputDevice}");
            return true;
        }

        private void RestartCapture()
        {
            WasapiLoopbackCapture? newLoopback = null;

            try
            {
                lock (_loopbackLock)
                {
                    if (_loopback != null)
                    {
                        if (_dataAvailableHandler != null)
                            _loopback.DataAvailable -= _dataAvailableHandler;
                        if (_recordingStoppedHandler != null)
                            _loopback.RecordingStopped -= _recordingStoppedHandler;

                        _dataAvailableHandler = null;
                        _recordingStoppedHandler = null;
                    }

                    try { _loopback?.StopRecording(); } catch { /* ignore */ }
                    _loopback?.Dispose();
                    _loopback = null;

                    if (_mp3Writer != null)
                    {
                        _mp3Writer.Dispose();
                        _mp3Writer = null;
                    }

                    CreateAndStartCapture(notifyHandlers: true);

                    if (_running)
                    {
                        _mp3Writer = new LameMP3FileWriter(_iceStream, _outputFormat, 320);
                    }

                    newLoopback = _loopback;
                }

                newLoopback?.StartRecording();
            }
            finally
            {
                _deviceSwitchInProgress = false;
            }
        }

        public void Start()
        {
            lock (_loopbackLock)
            {
                if (_running) return;

                if (_outputDevice == null || _iceStream == null) return;

                try
                {
                    CreateAndStartCapture(notifyHandlers: true);
                    _running = true;
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Failed to start loopback capture: {ex.Message}");
                    Stop();
                    throw;
                }
            }

            _loopback?.StartRecording();
        }

        public void Stop()
        {
            if (!_running) return;

            lock (_loopbackLock)
            {
                _running = false;

                try
                {
                    var handlers = _handlerSnapshot;

                    if (_loopback != null)
                    {
                        if (_dataAvailableHandler != null)
                        {
                            _loopback.DataAvailable -= _dataAvailableHandler;
                        }
                        if (_recordingStoppedHandler != null)
                        {
                            _loopback.RecordingStopped -= _recordingStoppedHandler;
                        }

                        try
                        {
                            _loopback.StopRecording();
                        }
                        catch (Exception ex)
                        {
                            Logging.LogError($"Error stopping loopback: {ex.Message}");
                        }

                        _loopback.Dispose();
                        _loopback = null;
                    }

                    // Null out handlers after unsubscribing
                    _dataAvailableHandler = null;
                    _recordingStoppedHandler = null;

                    if (_mp3Writer != null)
                    {
                        _mp3Writer.Dispose();
                        _mp3Writer = null;
                    }

                    foreach (var h in handlers)
                    {
                        try
                        {
                            h.OnStop();
                        }
                        catch (Exception ex)
                        {
                            Logging.LogError($"Error in handler OnStop: {ex.Message}");
                        }
                    }

                    _conversionBuffer = null;
                    _resampleBuffer = null;
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Error while stopping loopback capture: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _conversionBuffer = null;
            _resampleBuffer = null;
        }

        public void AddAudioHandler(IAudioHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            lock (_handlerLock)
            {
                var list = _handlerSnapshot.ToList();
                list.Add(handler);
                var newSnapshot = list.OrderBy(h => h.Order).ToArray();
                _handlerSnapshot = newSnapshot;
            }

            if (_running)
            {
                handler.OnStart(_outputFormat);
            }
        }

        public bool RemoveAudioHandler(IAudioHandler handler)
        {
            if (handler is null) return false;
            bool removed;
            lock (_handlerLock)
            {
                var list = _handlerSnapshot.ToList();
                removed = list.Remove(handler);
                if (removed)
                {
                    _handlerSnapshot = list.ToArray();
                }
            }

            if (removed && _running)
            {
                handler.OnStop();
            }
            return removed;
        }
    }
}
