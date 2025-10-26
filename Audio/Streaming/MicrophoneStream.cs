using GoombaCast.Audio.AudioHandlers;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GoombaCast.Audio.Streaming
{
    // Wrapper for MMDevice to represent input device the user can choose
    public class InputDevice
    {
        public MMDevice Device { get; }
        public InputDevice(MMDevice device)
        {
            Device = device;
        }

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

    public sealed class MicrophoneStream : IDisposable
    {
        private WasapiCapture? _mic;
        private LameMP3FileWriter? _mp3Writer;
        private IcecastStream? _iceStream;
        private InputDevice inputDevice;
        private volatile bool _running;

        private readonly WaveFormat _waveFormat = new WaveFormat(48000, 16, 2);
        private volatile bool _deviceSwitchInProgress;

        private readonly object _micLock = new();

        private readonly object _handlerLock = new();
        private AudioHandler[] _handlerSnapshot = Array.Empty<AudioHandler>();

        

        public MicrophoneStream(IcecastStream? icecastStream)
        {
            _iceStream = icecastStream;

            // Prefer default device; fall back to first active if needed
            try
            {
                inputDevice = InputDevice.GetDefaultInputDevice();
            }
            catch
            {
                var active = InputDevice.GetActiveInputDevices();
                inputDevice = active.FirstOrDefault() ?? throw new InvalidOperationException("No active input devices found.");
            }
        }

        // Expose currently selected input device
        public InputDevice CurrentInputDevice => inputDevice;

        public void StartBroadcast()
        {
            _iceStream.OpenAsync().Wait();
        }

        public void Start()
        {
            if (_running) return;

            // Prepare MP3 encoder: 48 kHz, 16-bit, stereo @ 320 kbps CBR
            _mp3Writer = new LameMP3FileWriter(_iceStream, _waveFormat, 320);

            CreateAndStartMic(notifyHandlers: true);

            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            lock (_micLock)
            {
                try { _mic?.StopRecording(); } catch { /* ignore */ }

                // Notify handlers capture is stopping
                var handlers = _handlerSnapshot;
                foreach (var h in handlers)
                    h.OnStop();

                _mic?.Dispose(); 
                _mic = null;
                _mp3Writer?.Dispose(); 
                _mp3Writer = null;
                _iceStream?.Dispose(); 
                _iceStream = null;
            }
        }

        public void Dispose() => Stop();

        // Change the input by device ID. If already running, restarts only the capture device.
        public bool SelectInputDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            var match = InputDevice.GetActiveInputDevices().FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (match is null) return false;

            return SetInputDevice(match);
        }

        // Change the input by InputDevice instance. If already running, restarts only the capture device.
        public bool SetInputDevice(InputDevice device)
        {
            if (device is null) return false;

            // No-op if same device
            if (string.Equals(inputDevice.Id, device.Id, StringComparison.OrdinalIgnoreCase))
                return true;

            inputDevice = device;
            
            RestartCapture();

            return true;
        }

        // Add/remove handlers. Safe to call before or during capture.
        public void AddAudioHandler(AudioHandler handler)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            AudioHandler[] newSnapshot;
            lock (_handlerLock)
            {
                var list = _handlerSnapshot.ToList();
                list.Add(handler);
                newSnapshot = list.OrderBy(h => h.Order).ToArray();
                _handlerSnapshot = newSnapshot;
            }

            // If already running, notify handler of current format
            if (_running && _mic != null)
            {
                handler.OnStart(_mic.WaveFormat);
            }
        }

        public bool RemoveAudioHandler(AudioHandler handler)
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
            lock (_micLock)
            {
                // Ensure any existing instance is properly cleaned up
                if (_mic != null)
                {
                    try { _mic.StopRecording(); } catch { /* ignore */ }
                    _mic.Dispose();
                    _mic = null;
                }

                _mic = new WasapiCapture(inputDevice.Device)
                {
                    ShareMode = AudioClientShareMode.Shared,
                    WaveFormat = _waveFormat
                };

                // Take snapshot of handlers before attaching events
                var handlers = _handlerSnapshot;

                if (notifyHandlers)
                {
                    // Notify handlers capture is starting with the selected format
                    foreach (var h in handlers)
                        h.OnStart(_waveFormat);
                }

                var micRef = _mic; // Capture in local for event handlers

                _mic.DataAvailable += (s, a) =>
                {
                    // Capture snapshot once at start of callback
                    var hs = _handlerSnapshot;
                    
                    // Verify mic hasn't been disposed
                    if (micRef != _mic) return;

                    for (int i = 0; i < hs.Length; i++)
                    {
                        var h = hs[i];
                        if (h.Enabled)
                            h.ProcessBuffer(a.Buffer, 0, a.BytesRecorded, micRef.WaveFormat);
                    }

                    lock (_micLock)
                    {
                        if (_mp3Writer != null && micRef == _mic)
                        {
                            _mp3Writer.Write(a.Buffer, 0, a.BytesRecorded);
                        }
                    }
                };

                _mic.RecordingStopped += (s, a) =>
                {
                    if (_deviceSwitchInProgress) return;
                    
                    // Ensure we're stopping the correct instance
                    if (micRef == _mic)
                    {
                        Stop();
                    }
                };

                _mic.StartRecording();
            }
        }

        private void RestartCapture()
        {
            try
            {
                try { _mic?.StopRecording(); } catch { /* ignore */ }
                _mic?.Dispose();
                _mic = null;

                // Keep MP3 writer and Icecast stream alive; just swap the input device
                CreateAndStartMic(notifyHandlers: false);
            }
            finally
            {
                _deviceSwitchInProgress = false;
            }
        }
    }
}
