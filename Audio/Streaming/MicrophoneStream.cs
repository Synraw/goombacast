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
    public class InputDevice {
        public MMDevice Device { get; }
        public InputDevice(MMDevice device) {
            Device = device;
        }
        public override string ToString() => Device.FriendlyName;

        // Get all active input devices
        public static List<InputDevice> GetActiveInputDevices() {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return devices.Select(d => new InputDevice(d)).ToList();
        }
    }

    public sealed class MicrophoneStream : IDisposable
    {
        private WasapiCapture? _mic;
        private LameMP3FileWriter? _mp3Writer;
        private IcecastStream _iceStream;
        private InputDevice inputDevice;
        private volatile bool _running;

        // Handler management (thread-safe snapshot pattern)
        private readonly object _handlerLock = new();
        private AudioHandler[] _handlerSnapshot = Array.Empty<AudioHandler>();

        public MicrophoneStream(IcecastStream icecastStream)
        {
            _iceStream = icecastStream;
            inputDevice = InputDevice.GetActiveInputDevices().First();
        }

        public void StartBroadcast()
        {
            _iceStream.OpenAsync().Wait();
        }

        public void Start()
        {
            if (_running) return;

            // 2) Prepare MP3 encoder: 48 kHz, 16-bit, stereo @ 320 kbps CBR
            var waveFormat = new WaveFormat(48000, 16, 2);
            _mp3Writer = new LameMP3FileWriter(_iceStream, waveFormat, 320);

            _mic = new WasapiCapture(inputDevice.Device)
            {
                ShareMode = AudioClientShareMode.Shared,
                WaveFormat = waveFormat
            };

            // Notify handlers capture is starting with the selected format
            var handlers = _handlerSnapshot;
            foreach (var h in handlers)
                h.OnStart(waveFormat);

            _mic.DataAvailable += (s, a) =>
            {
                // Give handlers a chance to inspect/modify PCM before encoding
                var hs = _handlerSnapshot; // snapshot for this callback
                for (int i = 0; i < hs.Length; i++)
                {
                    var h = hs[i];
                    if (h.Enabled)
                        h.ProcessBuffer(a.Buffer, 0, a.BytesRecorded, _mic!.WaveFormat);
                }

                _mp3Writer.Write(a.Buffer, 0, a.BytesRecorded);
                //_mp3Writer.Flush(); // keeps latency down (tradeoff: more calls)
            };

            _mic.RecordingStopped += (s, a) =>
            {
                // Ensure resources close cleanly if input stops
                Stop();
            };

            _running = true;
            _mic.StartRecording();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _mic?.StopRecording(); } catch { /* ignore */ }

            // Notify handlers capture is stopping
            var handlers = _handlerSnapshot;
            foreach (var h in handlers)
                h.OnStop();

            _mic?.Dispose(); _mic = null;
            _mp3Writer?.Dispose(); _mp3Writer = null;
            _iceStream?.Dispose(); _iceStream = null;
        }

        public void Dispose() => Stop();

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
    }
}
