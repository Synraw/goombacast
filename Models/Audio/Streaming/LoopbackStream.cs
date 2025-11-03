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
        private readonly WaveFormat _outputFormat = new(48000, 16, 2);

        private readonly object _loopbackLock = new();
        private readonly object _handlerLock = new();
        private IAudioHandler[] _handlerSnapshot = [];

        // Make buffers persistent and reuse them
        private byte[]? _conversionBuffer = new byte[19200]; // Pre-allocate for 100ms @ 48kHz
        private byte[]? _resampleBuffer = new byte[19200];
        private float[]? _filterState; // Store previous samples for continuity
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

            int bufferMilliseconds = 100;
            int bytesPerSecond = _loopback.WaveFormat.AverageBytesPerSecond;
            int bufferSize = (bytesPerSecond * bufferMilliseconds) / 1000;
            _conversionBuffer = new byte[bufferSize];

            // Align buffer to sample boundaries for optimal processing
            const int targetBufferMs = 20; // 20ms chunks for low latency
            int targetSamplesPerChannel = (_outputFormat.SampleRate * targetBufferMs) / 1000;

            // Ensure buffer is multiple of sample size (4 bytes for stereo 16-bit)
            int alignedBufferSize = targetSamplesPerChannel * 4;

            int sourceBytesPerSecond = _loopback.WaveFormat.AverageBytesPerSecond;
            int sourceBufferSize = (sourceBytesPerSecond * targetBufferMs) / 1000;
            sourceBufferSize = (sourceBufferSize + 3) & ~3; // Align to 4-byte boundary

            int requiredSize = (sourceBytesPerSecond * targetBufferMs) / 1000;
            if (_conversionBuffer.Length < requiredSize * 2)
            {
                _conversionBuffer = new byte[requiredSize * 2];
            }
            _resampleBuffer = new byte[alignedBufferSize * 2];

            var handlers = _handlerSnapshot;

            if (notifyHandlers)
            {
                foreach (var h in handlers)
                {
                    h.OnStart(_outputFormat);
                }
            }

            var loopbackRef = _loopback;
            var sourceChannels = _loopback.WaveFormat.Channels;
            var sourceSampleRate = _loopback.WaveFormat.SampleRate;

            _dataAvailableHandler = (s, a) =>
            {
                if (loopbackRef != _loopback || !_running) return;

                try
                {
                    var hs = _handlerSnapshot;

                    // Step 1: Convert float32 to int16 at source sample rate
                    int stereoBytes = (a.BytesRecorded / sourceChannels) * 2;
                    if (_conversionBuffer.Length < stereoBytes)
                    {
                        Array.Resize(ref _conversionBuffer, stereoBytes);
                    }

                    // Update the DataAvailable handler to use the static utilities:
                    int convertedLength = AudioFormatConverter.ConvertFloat32ToStereoInt16(
                        a.Buffer,
                        _conversionBuffer,
                        a.BytesRecorded,
                        sourceChannels,
                        _ditherRng ??= new Random());

                    // Step 2: Resample from source rate to 48kHz if needed
                    byte[] outputBuffer;
                    int outputLength;

                    if (sourceSampleRate != _outputFormat.SampleRate)
                    {
                        // Apply anti-aliasing filter before resampling
                        AudioResampler.ApplyAntiAliasingFilter(_conversionBuffer, convertedLength, sourceSampleRate, ref _filterState!);

                        outputLength = AudioResampler.ResampleTo48kHz(
                            _conversionBuffer,
                            convertedLength,
                            sourceSampleRate,
                            _resampleBuffer,
                            ref _resampleBuffer);
                        outputBuffer = _resampleBuffer;
                    }
                    else
                    {
                        outputBuffer = _conversionBuffer;
                        outputLength = convertedLength;
                    }

                    // Step 3: Send to handlers
                    for (int i = 0; i < hs.Length; i++)
                    {
                        var h = hs[i];
                        if (h.Enabled)
                        {
                            h.ProcessBuffer(outputBuffer, 0, outputLength, _outputFormat);
                        }
                    }

                    lock (_loopbackLock)
                    {
                        if (_mp3Writer != null && loopbackRef == _loopback)
                        {
                            _mp3Writer.Write(outputBuffer, 0, outputLength);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Error processing loopback audio: {ex.Message}");
                    Stop();
                }
            };

            _recordingStoppedHandler = (s, a) =>
            {
                if (_deviceSwitchInProgress) return;

                if (loopbackRef == _loopback)
                {
                    Stop();
                }
            };

            _loopback.DataAvailable += _dataAvailableHandler;
            _loopback.RecordingStopped += _recordingStoppedHandler;

            _loopback.StartRecording();
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

                try { _loopback.StopRecording(); } catch { /* ignore */ }
                _loopback.Dispose();
                _loopback = null;
            }

            _conversionBuffer = null;
            _resampleBuffer = null;

            // Reset filter state to prevent artifacts on next start (NEW)
            if (_filterState != null)
            {
                Array.Clear(_filterState, 0, _filterState.Length);
            }
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
            try
            {
                lock (_loopbackLock)
                {
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
                }
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

                    CleanupCapture();

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

                    // Reset filter state to prevent artifacts on next start (NEW)
                    if (_filterState != null)
                    {
                        Array.Clear(_filterState, 0, _filterState.Length);
                    }
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
