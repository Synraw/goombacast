using GoombaCast.Models.Audio.AudioHandlers;
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

                    int convertedLength = ConvertFloatToStereoInt16(
                        a.Buffer,
                        _conversionBuffer,
                        a.BytesRecorded,
                        sourceChannels);

                    // Step 2: Resample from source rate to 48kHz if needed
                    byte[] outputBuffer;
                    int outputLength;

                    if (sourceSampleRate != _outputFormat.SampleRate)
                    {
                        // Apply anti-aliasing filter before resampling
                        ApplyAntiAliasingFilter(_conversionBuffer, convertedLength, sourceSampleRate);

                        outputLength = ResampleTo48kHz(
                            _conversionBuffer,
                            convertedLength,
                            sourceSampleRate,
                            _resampleBuffer);
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

        private Random? _ditherRng;

        private unsafe int ConvertFloatToStereoInt16(byte[] source, byte[] dest, int sourceBytes, int sourceChannels)
        {
            if (_ditherRng == null)
                _ditherRng = new Random();

            int samplesPerChannel = sourceBytes / (4 * sourceChannels);
            int outputSamples = samplesPerChannel * 2;

            fixed (byte* sourcePtr = source)
            fixed (byte* destPtr = dest)
            {
                float* floatPtr = (float*)sourcePtr;
                short* shortPtr = (short*)destPtr;

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    float leftSum = 0;
                    float rightSum = 0;

                    for (int ch = 0; ch < sourceChannels; ch++)
                    {
                        float sample = floatPtr[i * sourceChannels + ch];
                        if (ch % 2 == 0)
                            leftSum += sample;
                        else
                            rightSum += sample;
                    }

                    leftSum /= (sourceChannels + 1) / 2;
                    rightSum /= sourceChannels / 2;

                    leftSum = Math.Max(-1.0f, Math.Min(1.0f, leftSum));
                    rightSum = Math.Max(-1.0f, Math.Min(1.0f, rightSum));

                    // Add TPDF dithering (triangular probability density function)
                    float ditherL = ((float)_ditherRng.NextDouble() + (float)_ditherRng.NextDouble() - 1.0f) * 0.5f;
                    float ditherR = ((float)_ditherRng.NextDouble() + (float)_ditherRng.NextDouble() - 1.0f) * 0.5f;

                    shortPtr[i * 2] = (short)((leftSum * 32767.0f) + ditherL);
                    shortPtr[i * 2 + 1] = (short)((rightSum * 32767.0f) + ditherR);
                }
            }

            return outputSamples * 2;
        }

        /// <summary>
        /// Resamples 16-bit stereo PCM using Sinc interpolation for better quality
        /// </summary>
        private unsafe int ResampleTo48kHz(byte[] source, int sourceBytes, int sourceSampleRate, byte[] dest)
        {
            const int targetSampleRate = 48000;

            int sourceSamples = sourceBytes / 4; // 2 channels * 2 bytes per sample
            double ratio = (double)sourceSampleRate / targetSampleRate;
            int targetSamples = (int)(sourceSamples / ratio);
            int targetBytes = targetSamples * 4;

            if (dest.Length < targetBytes)
            {
                Array.Resize(ref _resampleBuffer, targetBytes);
                dest = _resampleBuffer;
            }

            fixed (byte* srcPtr = source)
            fixed (byte* dstPtr = dest)
            {
                short* srcShort = (short*)srcPtr;
                short* dstShort = (short*)dstPtr;

                // Windowed Sinc interpolation (higher quality than linear)
                const int sincRadius = 4; // Wider window = better quality, more CPU

                for (int i = 0; i < targetSamples; i++)
                {
                    double srcPos = i * ratio;
                    int srcIndex = (int)srcPos;
                    double frac = srcPos - srcIndex;

                    float leftSum = 0;
                    float rightSum = 0;
                    float weightSum = 0;

                    // Apply windowed sinc filter
                    for (int j = -sincRadius; j <= sincRadius; j++)
                    {
                        int sampleIdx = srcIndex + j;
                        if (sampleIdx < 0 || sampleIdx >= sourceSamples)
                            continue;

                        double x = frac - j;
                        float weight = (float)SincWindow(x, sincRadius);

                        leftSum += srcShort[sampleIdx * 2] * weight;
                        rightSum += srcShort[sampleIdx * 2 + 1] * weight;
                        weightSum += weight;
                    }

                    // Normalize and clamp
                    if (weightSum > 0)
                    {
                        leftSum /= weightSum;
                        rightSum /= weightSum;
                    }

                    dstShort[i * 2] = (short)Math.Clamp((int)leftSum, short.MinValue, short.MaxValue);
                    dstShort[i * 2 + 1] = (short)Math.Clamp((int)rightSum, short.MinValue, short.MaxValue);
                }
            }

            return targetBytes;
        }

        /// <summary>
        /// Windowed Sinc function for high-quality resampling
        /// </summary>
        private static double SincWindow(double x, int radius)
        {
            if (Math.Abs(x) < 0.0001)
                return 1.0;

            double pix = Math.PI * x;
            double sinc = Math.Sin(pix) / pix;

            // Blackman window for smoothing
            double window = 0.42 - 0.5 * Math.Cos(Math.PI * (x + radius) / radius)
                        + 0.08 * Math.Cos(2 * Math.PI * (x + radius) / radius);

            return sinc * window;
        }

        // Fix the anti-aliasing filter to process channels independently
        private unsafe void ApplyAntiAliasingFilter(byte[] buffer, int length, int sampleRate)
        {
            const int filterOrder = 8;

            // Separate filter states for left and right channels
            if (_filterState == null || _filterState.Length != filterOrder * 2)
                _filterState = new float[filterOrder * 2];

            int samples = length / 4; // Stereo 16-bit (2 bytes per sample * 2 channels)

            fixed (byte* bufPtr = buffer)
            fixed (float* statePtr = _filterState)
            {
                short* shortPtr = (short*)bufPtr;

                float* leftState = statePtr;
                float* rightState = statePtr + filterOrder;

                // Process each stereo frame
                for (int i = 0; i < samples; i++)
                {
                    // Process left channel
                    float leftSample = shortPtr[i * 2];
                    float leftFiltered = leftSample * 0.5f;

                    for (int j = 0; j < filterOrder - 1; j++)
                    {
                        leftFiltered += leftState[j] * (0.5f / filterOrder);
                        leftState[j] = leftState[j + 1];
                    }
                    leftState[filterOrder - 1] = leftSample;

                    shortPtr[i * 2] = (short)Math.Clamp((int)leftFiltered, short.MinValue, short.MaxValue);

                    // Process right channel independently
                    float rightSample = shortPtr[i * 2 + 1];
                    float rightFiltered = rightSample * 0.5f;

                    for (int j = 0; j < filterOrder - 1; j++)
                    {
                        rightFiltered += rightState[j] * (0.5f / filterOrder);
                        rightState[j] = rightState[j + 1];
                    }
                    rightState[filterOrder - 1] = rightSample;

                    shortPtr[i * 2 + 1] = (short)Math.Clamp((int)rightFiltered, short.MinValue, short.MaxValue);
                }
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
