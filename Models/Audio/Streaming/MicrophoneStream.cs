using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Models.Audio.AudioProcessing; // ADD THIS
using GoombaCast.Services;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using static System.Windows.Forms.DataFormats;

namespace GoombaCast.Models.Audio.Streaming
{
    // Wrapper for MMDevice to represent input device the user can choose
    public sealed class InputDevice(MMDevice device)
    {
        public MMDevice Device { get; } = device;

        // Stable identifier suitable for persistence
        public string Id => Device.ID;

        public override string ToString() => Device.FriendlyName;

        // Get all active input devices
        public static List<InputDevice> GetActiveInputDevices()
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return devices.Select(d => new InputDevice(d)).ToList();
        }

        // Get OS default input device (Multimedia role)
        public static InputDevice GetDefaultInputDevice()
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return new InputDevice(device);
        }
    }

    public sealed class MicrophoneStream(IcecastStream icecastStream, InputDevice? device) : IAudioStream
    {
        private WasapiCapture? _mic;
        private LameMP3FileWriter? _mp3Writer;
        private IcecastStream _iceStream = icecastStream;
        private InputDevice? _inputDevice = device ?? InputDevice.GetActiveInputDevices().FirstOrDefault();
        private volatile bool _running = false;

        // Fixed output format to match mixer (48kHz, 16-bit, stereo)
        private readonly WaveFormat _outputFormat = new(48000, 16, 2);
        private volatile bool _deviceSwitchInProgress;

        private readonly object _micLock = new();
        private readonly object _handlerLock = new();
        private IAudioHandler[] _handlerSnapshot = [];

        // Buffers for format conversion
        private byte[]? _conversionBuffer = new byte[19200];
        private byte[]? _resampleBuffer = new byte[19200];
        private float[]? _filterState;
        private Random? _ditherRng;

        public InputDevice? CurrentInputDevice => _inputDevice;
        public WaveFormat WaveFormat => _outputFormat; // Always return 48kHz format
        public bool IsRunning => _running;

        public void Start()
        {
            lock (_micLock)
            {
                if (_running) return;

                if (_iceStream == null)
                    throw new InvalidOperationException("IcecastStream not initialized");

                try
                {
                    _mp3Writer = new LameMP3FileWriter(_iceStream, _outputFormat, 320);
                    CreateAndStartMic(notifyHandlers: true);
                    _running = true;
                }
                catch
                {
                    // Cleanup on failure
                    _mp3Writer?.Dispose();
                    _mp3Writer = null;
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_micLock)
            {
                if (!_running) return;
                _running = false;

                var handlers = _handlerSnapshot;

                try
                {
                    if (_mic != null)
                    {
                        try
                        {
                            _mic.StopRecording();
                        }
                        catch (Exception ex)
                        {
                            Logging.LogError($"Error stopping recording: {ex.Message}");
                        }
                        _mic.Dispose();
                        _mic = null;
                    }
                }
                finally
                {
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

                    _mp3Writer?.Dispose();
                    _mp3Writer = null;
                }
            }
        }

        public void Dispose() => Stop();

        public bool ChangeDevice(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            var match = InputDevice.GetActiveInputDevices().FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (match is null) return false;

            return SetInputDevice(match);
        }

        private bool SetInputDevice(InputDevice device)
        {
            if (device is null) return false;

            if (string.Equals(_inputDevice?.Id, device.Id, StringComparison.OrdinalIgnoreCase))
                return true;

            _deviceSwitchInProgress = true;
            _inputDevice = device;
            RestartCapture();
            Logging.Log($"Selected input device {_inputDevice}");
            return true;
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
                // Give the handler a chance to cleanup
                handler.OnStop();
            }
            return removed;
        }

        private void CreateAndStartMic(bool notifyHandlers)
        {
            // Clean up existing instance
            DisposeCurrentMic();

            // Initialize new capture device
            _mic = new WasapiCapture(_inputDevice?.Device)
            {
                ShareMode = AudioClientShareMode.Shared
            };

            // Allocate buffers based on native and output formats
            var nativeFormat = _mic.WaveFormat;
            InitializeBuffers(nativeFormat);

            // Notify handlers if requested
            if (notifyHandlers)
            {
                NotifyHandlersStart();
            }

            // Attach event handlers and start recording
            AttachEventHandlers(nativeFormat);
            _mic.StartRecording();
        }

        private void DisposeCurrentMic()
        {
            if (_mic != null)
            {
                try { _mic.StopRecording(); } catch { /* ignore */ }
                _mic.Dispose();
                _mic = null;
            }
        }

        private void InitializeBuffers(WaveFormat nativeFormat)
        {
            var settings = SettingsService.Default.Settings;
            int targetBufferMs = settings.AudioBufferMs;

            // Calculate source buffer size
            int sourceBytesPerSecond = nativeFormat.AverageBytesPerSecond;
            int sourceBufferSize = (sourceBytesPerSecond * targetBufferMs) / 1000;
            sourceBufferSize = (sourceBufferSize + 3) & ~3; // Align to 4 bytes

            if (_conversionBuffer == null || _conversionBuffer.Length < sourceBufferSize * 2)
            {
                _conversionBuffer = new byte[sourceBufferSize * 2];
            }

            // Calculate output buffer size
            int outputSamplesPerChannel = (_outputFormat.SampleRate * targetBufferMs) / 1000;
            int outputBufferSize = outputSamplesPerChannel * 4; // 2 channels * 2 bytes per sample

            if (_resampleBuffer == null || _resampleBuffer.Length < outputBufferSize * 2)
            {
                _resampleBuffer = new byte[outputBufferSize * 2];
            }
        }

        private void NotifyHandlersStart()
        {
            var handlers = _handlerSnapshot;
            foreach (var handler in handlers)
            {
                handler.OnStart(_outputFormat);
            }
        }

        private void AttachEventHandlers(WaveFormat nativeFormat)
        {
            var micRef = _mic;
            var sourceChannels = nativeFormat.Channels;
            var sourceSampleRate = nativeFormat.SampleRate;
            var sourceBitsPerSample = nativeFormat.BitsPerSample;

            _mic!.DataAvailable += (s, a) =>
            {
                if (micRef != _mic || !_running) return;

                try
                {
                    ProcessAudioData(a.Buffer, a.BytesRecorded, sourceChannels, sourceSampleRate, sourceBitsPerSample, nativeFormat.Encoding, micRef);
                }
                catch (Exception ex)
                {
                    Logging.LogError($"Error processing microphone audio: {ex.Message}");
                }
            };

            _mic!.RecordingStopped += (s, a) =>
            {
                if (_deviceSwitchInProgress) return;

                // Ensure we're stopping the correct instance
                if (micRef == _mic)
                {
                    Stop();
                }
            };
        }

        private void ProcessAudioData(byte[] inputBuffer, int bytesRecorded, int sourceChannels, int sourceSampleRate, int sourceBitsPerSample, WaveFormatEncoding encoding, WasapiCapture micRef)
        {
            var handlers = _handlerSnapshot;

            // Step 1: Convert to 16-bit stereo at native sample rate
            int convertedLength = ConvertToStereoInt16(inputBuffer, bytesRecorded, sourceChannels, sourceBitsPerSample, encoding);

            // Step 2: Resample to 48kHz if needed
            (byte[] outputBuffer, int outputLength) = ResampleIfNeeded(convertedLength, sourceSampleRate);

            // Step 3: Send to handlers
            ProcessHandlers(handlers, outputBuffer, outputLength);

            // Step 4: Write to MP3 encoder
            WriteToEncoder(outputBuffer, outputLength, micRef);
        }

        private int ConvertToStereoInt16(byte[] inputBuffer, int bytesRecorded, int sourceChannels, int sourceBitsPerSample, WaveFormatEncoding encoding)
        {
            if (sourceBitsPerSample == 32 && encoding == WaveFormatEncoding.IeeeFloat)
            {
                return AudioFormatConverter.ConvertFloat32ToStereoInt16(
                    inputBuffer,
                    _conversionBuffer!,
                    bytesRecorded,
                    sourceChannels,
                    _ditherRng ??= new Random());
            }
            else if (sourceBitsPerSample == 16)
            {
                return AudioFormatConverter.ConvertInt16ToStereo(
                    inputBuffer,
                    _conversionBuffer!,
                    bytesRecorded,
                    sourceChannels);
            }
            else if (sourceBitsPerSample == 24)
            {
                return AudioFormatConverter.ConvertInt24ToStereoInt16(
                    inputBuffer,
                    _conversionBuffer!,
                    bytesRecorded,
                    sourceChannels);
            }
            else
            {
                Logging.LogWarning($"Unsupported microphone bit depth: {sourceBitsPerSample}");
                return 0;
            }
        }

        private (byte[] buffer, int length) ResampleIfNeeded(int convertedLength, int sourceSampleRate)
        {
            if (sourceSampleRate != _outputFormat.SampleRate)
            {
                AudioResampler.ApplyAntiAliasingFilter(_conversionBuffer!, convertedLength, sourceSampleRate, ref _filterState!);

                int outputLength = AudioResampler.ResampleTo48kHz(
                    _conversionBuffer!,
                    convertedLength,
                    sourceSampleRate,
                    _resampleBuffer!,
                    ref _resampleBuffer!);

                return (_resampleBuffer, outputLength);
            }

            return (_conversionBuffer!, convertedLength);
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

        private void WriteToEncoder(byte[] buffer, int length, WasapiCapture micRef)
        {
            lock (_micLock)
            {
                if (_mp3Writer != null && micRef == _mic)
                {
                    _mp3Writer.Write(buffer, 0, length);
                }
            }
        }

        private void RestartCapture()
        {
            try
            {
                lock (_micLock)
                {
                    try 
                    { 
                        _mic?.StopRecording(); 
                    } catch (Exception e) { 
                        Logging.LogWarning($"Error stopping microphone during device switch: {e.Message}");
                    }
                    _mic?.Dispose();
                    _mic = null;

                    // Keep MP3 writer and Icecast stream alive; just swap the input device
                    CreateAndStartMic(notifyHandlers: false);
                }
            }
            finally
            {
                _deviceSwitchInProgress = false;
            }
        }
    }
}
