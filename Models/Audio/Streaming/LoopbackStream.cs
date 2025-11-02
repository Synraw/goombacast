using GoombaCast.Models.Audio.AudioHandlers;
using GoombaCast.Services;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        private readonly object _loopbackLock = new();
        private readonly object _handlerLock = new();
        private IAudioHandler[] _handlerSnapshot = [];

        private byte[]? _conversionBuffer;
        private EventHandler<WaveInEventArgs>? _dataAvailableHandler;
        private EventHandler<StoppedEventArgs>? _recordingStoppedHandler;

        public OutputDevice? CurrentOutputDevice => _outputDevice;
        public WaveFormat? WaveFormat => _loopback?.WaveFormat;
        public bool IsRunning => _running;

        private void CreateAndStartCapture(bool notifyHandlers)
        {
            if (_loopback != null)
            {
                CleanupCapture();
            }

            _loopback = new WasapiLoopbackCapture(_outputDevice?.Device);

            var stereoFormat = new WaveFormat(_loopback!.WaveFormat.SampleRate, 16, 2);
            _mp3Writer = new LameMP3FileWriter(_iceStream, stereoFormat, 320);

            int bufferMilliseconds = 100;
            int bytesPerSecond = _loopback.WaveFormat.AverageBytesPerSecond;
            int bufferSize = (bytesPerSecond * bufferMilliseconds) / 1000;
            _conversionBuffer = new byte[bufferSize];

            var handlers = _handlerSnapshot;

            if (notifyHandlers)
            {
                foreach (var h in handlers)
                {
                    h.OnStart(stereoFormat);
                }
            }

            var loopbackRef = _loopback;
            var sourceChannels = _loopback.WaveFormat.Channels;

            _dataAvailableHandler = (s, a) =>
            {
                if (loopbackRef != _loopback || !_running) return;

                try
                {
                    var hs = _handlerSnapshot;

                    int stereoBytes = (a.BytesRecorded / sourceChannels) * 2;
                    if (_conversionBuffer.Length < stereoBytes)
                    {
                        Array.Resize(ref _conversionBuffer, stereoBytes);
                    }

                    int convertedLength = ConvertFloatToStereoInt16(a.Buffer, _conversionBuffer, a.BytesRecorded, sourceChannels);

                    for (int i = 0; i < hs.Length; i++)
                    {
                        var h = hs[i];
                        if (h.Enabled)
                        {
                            h.ProcessBuffer(_conversionBuffer, 0, convertedLength, stereoFormat);
                        }
                    }

                    lock (_loopbackLock)
                    {
                        if (_mp3Writer != null && loopbackRef == _loopback)
                        {
                            _mp3Writer.Write(_conversionBuffer, 0, convertedLength);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Log($"Error processing audio data: {ex.Message}");
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
        }

        private unsafe int ConvertFloatToStereoInt16(byte[] source, byte[] dest, int sourceBytes, int sourceChannels)
        {
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

                    shortPtr[i * 2] = (short)(leftSum * 32767.0f);
                    shortPtr[i * 2 + 1] = (short)(rightSum * 32767.0f);
                }
            }

            return outputSamples * 2; 
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
                        _mp3Writer = new LameMP3FileWriter(_iceStream, _loopback!.WaveFormat, 320);
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
                    Logging.Log($"Failed to start loopback capture: {ex.Message}");
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
                            Logging.Log($"Error in handler OnStop: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Log($"Error while stopping loopback capture: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _conversionBuffer = null;
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

            if (_running && _loopback?.WaveFormat != null)
            {
                handler.OnStart(_loopback.WaveFormat);
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
