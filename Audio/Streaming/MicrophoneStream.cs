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
    public sealed class MicrophoneIcecastStreamer : IDisposable
    {
        private WasapiCapture? _mic;
        private LameMP3FileWriter? _mp3Writer;
        private IcecastStream _iceStream;
        private volatile bool _running;

        public MicrophoneIcecastStreamer(IcecastStream icecastStream)
        {
            _iceStream = icecastStream;
        }

        public static MMDevice? ChooseInputDevice()
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

            Console.WriteLine("Available capture devices:");
            for (int i = 0; i < devices.Count; i++)
            {
                Console.WriteLine($"{i}: {devices[i].FriendlyName}");
            }

            Console.Write("Select device index: ");
            if (!int.TryParse(Console.ReadLine(), out int index) || index < 0 || index >= devices.Count)
            {
                Console.WriteLine("Invalid selection.");
                return null;
            }

            var device = devices[index];
            Console.WriteLine($"Using: {device.FriendlyName}");
            return device;
        }

        public void Start()
        {
            if (_running) return;

            var device = ChooseInputDevice();

            _iceStream.OpenAsync().Wait();

            // 2) Prepare MP3 encoder: 48 kHz, 16-bit, stereo @ 320 kbps CBR
            var waveFormat = new WaveFormat(48000, 16, 2);
            _mp3Writer = new LameMP3FileWriter(_iceStream, waveFormat, 320);

            _mic = new WasapiCapture(device)
            {
                ShareMode = AudioClientShareMode.Shared,
                WaveFormat = waveFormat
            };

            _mic.DataAvailable += (s, a) =>
            {
                // Push raw PCM to MP3 writer; it will write encoded frames to _shoutStream

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

            _mic?.Dispose(); _mic = null;
            _mp3Writer?.Dispose(); _mp3Writer = null;
            _iceStream?.Dispose(); _iceStream = null;
        }

        public void Dispose() => Stop();
    }
}
